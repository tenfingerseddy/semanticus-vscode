// A change set on a plan item: propose, keep, drop, approve, cancel (Jev M3).
//
// What these tests hold, one line each:
//   propose works the exact changes out and WRITES NOTHING, so the revision does not move
//   a line carries a real before, a real after and where it came from, never a reworded finding
//   keeping and dropping a line changes the digest and nothing else
//   approve applies exactly the lines that were kept, in one undo step, and the gap goes on the next compose
//   approve refuses every way the model can have moved underneath it, and names which
//   approve of a digest that is not the one on the plan applies nothing
//   approving the same digest twice applies it once
//   a kept line that cannot be made puts the WHOLE set back
//   cancel changes nothing, and cancelling something already applied says so rather than pretending
//   an assistant's change lands on the item it serves, and opens an item of its own when none serves it
//   reading, proposing, keeping, dropping and cancelling are free; running a row for you is Pro
//   a model exchanged underneath a verb lands on neither model and answers a sentence, not an exception
//   bulk apply makes every listed item in one step and one undo; one failing line or one stale digest writes nothing
//
// The fixture is DecisionFixture, the same one ModelPlanTests uses, because a change set needs exactly what a
// plan needs: a scratch home, a scratch workspace and a saved model with a durable identity (what is
// remembered about a set is kept beside the model).

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class ModelPlanChangesTests
    {
        /// <summary>A model with real, deterministic gaps: an unhidden foreign key (VIS-FK), a geographic
        /// column with no data category (CAT-GEO) and a measure nothing uses (the cleanup sweep). Small on
        /// purpose, so a failure names one thing.</summary>
        private static async Task<DecisionFixture> GappyAsync(string file, bool pro = true)
        {
            var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateTableAsync("Customer", "human");
            await f.Engine.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateColumnAsync("table:Customer", "Name", "String", null, "human");
            await f.Engine.CreateColumnAsync("table:Customer", "City", "String", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "ProductKey", "Int64", null, "human");
            await f.Engine.CreateTableAsync("Product", "human");
            await f.Engine.CreateColumnAsync("table:Product", "ProductKey", "Int64", null, "human");
            await f.Engine.CreateColumnAsync("table:Product", "Name", "String", null, "human");
            await f.Engine.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await f.Engine.CreateRelationshipAsync("column:Sales/ProductKey", "column:Product/ProductKey", null, true, "human");
            // A relationship endpoint that adds up. FMT-SUMMARIZE fires on it, which gives the Formatting row
            // a set_summarize_by line, and that line's value is an ENUM, so it is the one line in the whole
            // plan that can be made to fail through the plan alone, without touching the model.
            await f.Engine.SetColumnMetadataAsync("column:Sales/ProductKey", null, "Sum", null, null, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Old Total", "SUM ( Sales[Amount] )", "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        private static ModelPlanItem SafeFixRow(ModelPlanView plan) =>
            plan.Items.First(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal));

        private static ModelPlanItem Row(ModelPlanView plan, string id) =>
            plan.Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));

        // ---- propose writes nothing ------------------------------------------------------------------

        [Fact]
        public async Task Propose_works_the_changes_out_and_writes_nothing()
        {
            using var f = await GappyAsync("ChangePropose.bim");
            var before = await f.Engine.GetModelPlanAsync();
            var row = SafeFixRow(before);
            Assert.Equal(ModelPlanItemStates.Gap, row.State);
            Assert.Null(row.ChangeSet);
            var revisionBefore = before.Revision;

            var result = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");

            // Nothing was written. The revision is the whole of that claim: a mutation would have moved it.
            Assert.False(result.Performed);
            Assert.Equal(revisionBefore, result.Plan.Revision);

            var after = Row(result.Plan, row.Id);
            Assert.Equal(ModelPlanItemStates.Proposed, after.State);
            Assert.NotNull(after.ChangeSet);
            Assert.Equal(ModelPlanChangeStates.Proposed, after.ChangeSet.State);
            Assert.NotEmpty(after.ChangeSet.Lines);
            Assert.False(string.IsNullOrWhiteSpace(after.ChangeSet.Digest));
            Assert.Equal(revisionBefore, after.ChangeSet.Revision);
            Assert.False(after.ChangeSet.Stale);
            Assert.Contains("Nothing has been written", after.ChangeSet.Sentence, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Every_line_carries_a_before_an_after_and_where_it_came_from()
        {
            using var f = await GappyAsync("ChangeLines.bim");
            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());
            var result = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");
            var set = Row(result.Plan, row.Id).ChangeSet;

            Assert.All(set.Lines, l =>
            {
                Assert.False(string.IsNullOrWhiteSpace(l.Id));
                Assert.False(string.IsNullOrWhiteSpace(l.PlanItemId));   // what approve addresses
                Assert.False(string.IsNullOrWhiteSpace(l.Target));
                Assert.False(string.IsNullOrWhiteSpace(l.After));        // a diff is a change or it is nothing
                Assert.NotNull(l.Provenance);
                Assert.Equal(ModelPlanChangeKinds.Rule, l.Provenance.Kind);
                Assert.False(string.IsNullOrWhiteSpace(l.Provenance.Rule));
                // A rule is deterministic, so it never carries a confidence. The contract's provenance type
                // refuses that combination outright and this is the wire proof of it.
                Assert.Null(l.Provenance.Confidence);
                Assert.DoesNotContain("—", l.Line, StringComparison.Ordinal);
            });
        }

        [Fact]
        public async Task A_second_propose_replaces_the_first_rather_than_stacking()
        {
            using var f = await GappyAsync("ChangeRepropose.bim");
            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());
            var first = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");
            var firstCount = Row(first.Plan, row.Id).ChangeSet.Lines.Length;

            var second = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");
            var set = Row(second.Plan, row.Id).ChangeSet;

            Assert.Equal(firstCount, set.Lines.Length);
            Assert.Equal(set.Lines.Length, set.Lines.Select(l => l.Id).Distinct(StringComparer.Ordinal).Count());
        }

        // ---- keep and drop ----------------------------------------------------------------------------

        [Fact]
        public async Task Dropping_a_line_changes_the_digest_and_nothing_else()
        {
            using var f = await GappyAsync("ChangeDrop.bim");
            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());
            var proposed = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");
            var set = Row(proposed.Plan, row.Id).ChangeSet;
            Assert.True(set.Lines.Length >= 2, "this fixture needs a row with more than one line");
            Assert.Equal(set.Lines.Length, set.KeptCount);
            var digestBefore = set.Digest;
            var revisionBefore = proposed.Plan.Revision;
            var dropped = set.Lines[0];

            await f.Engine.SetPlanItemAsync(dropped.PlanItemId, null, approved: false, "human");

            var after = Row(await f.Engine.GetModelPlanAsync(), row.Id).ChangeSet;
            Assert.Equal(revisionBefore, after.Revision);                       // nothing was written
            Assert.Equal(set.Lines.Length, after.Lines.Length);                 // the line is DROPPED, not gone
            Assert.False(after.Lines.Single(l => l.Id == dropped.Id).Kept);
            Assert.Equal(set.Lines.Length - 1, after.KeptCount);
            Assert.NotEqual(digestBefore, after.Digest);                        // so the old approval cannot be honoured

            // And keeping it again puts the digest back, because the digest is a function of the lines.
            await f.Engine.SetPlanItemAsync(dropped.PlanItemId, null, approved: true, "human");
            Assert.Equal(digestBefore, Row(await f.Engine.GetModelPlanAsync(), row.Id).ChangeSet.Digest);
        }

        // ---- the assistant's door: the words for a plan row name every kept line and its new value (Q1, the describe hook) ---------

        [Fact]
        public async Task The_question_for_a_plan_row_names_every_kept_line_and_its_new_value()
        {
            using var f = await GappyAsync("ChangeWords.bim");
            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());
            var proposed = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");
            var set = Row(proposed.Plan, row.Id).ChangeSet;
            Assert.NotEmpty(set.Lines);

            var tool = typeof(IEngine).GetMethod("ModelPlanActionAsync");
            var approve = await f.Engine.GateDescribeModelPlanActionAsync(new GateCall
            {
                Engine = f.Engine, Method = tool, Parameters = tool.GetParameters(), Args = new object[] { row.Id, "approve", "agent", set.Digest },
            });
            var items = typeof(IEngine).GetMethod("ApplyPlanItemsAsync");
            var listed = await f.Engine.GateDescribePlanItemDigests(new GateCall
            {
                Engine = f.Engine, Method = items, Parameters = items.GetParameters(),
                Args = new object[] { new[] { new PlanItemDigest { Id = row.Id, Digest = set.Digest } }, "agent", null },
            });
            foreach (var words in new[] { approve, listed })
            {
                foreach (var line in set.Lines.Where(l => l.Kept))
                {
                    Assert.Contains(line.Target.Split('/').Last(), words, StringComparison.OrdinalIgnoreCase);
                    if (!string.IsNullOrEmpty(line.After)) Assert.Contains(line.After, words, StringComparison.Ordinal);
                }
            }
        }

        // ---- approve ----------------------------------------------------------------------------------

        [Fact]
        public async Task Approve_applies_exactly_the_kept_lines_in_one_undo_step()
        {
            using var f = await GappyAsync("ChangeApprove.bim");
            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());
            var proposed = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");
            var set = Row(proposed.Plan, row.Id).ChangeSet;
            Assert.True(set.Lines.Length >= 2);

            var dropped = set.Lines[0];
            await f.Engine.SetPlanItemAsync(dropped.PlanItemId, null, approved: false, "human");
            var ready = Row(await f.Engine.GetModelPlanAsync(), row.Id).ChangeSet;
            var keptCount = ready.KeptCount;
            var revisionBefore = ready.Revision;

            var result = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "human", ready.Digest);

            Assert.True(result.Performed);
            Assert.NotEqual(revisionBefore, result.Revision);
            Assert.Contains("Undo puts", result.Sentence, StringComparison.OrdinalIgnoreCase);

            // ONE undo entry: a SINGLE undo puts every one of the applied lines back, so the row that had
            // gone (or shrunk) is open again with the same id. Two undo steps would mean two batches.
            Assert.True(keptCount >= 1);
            await f.Engine.UndoAsync("human");
            var back = await f.Engine.GetModelPlanAsync();
            var reopened = Row(back, row.Id);
            Assert.NotNull(reopened);
            Assert.NotEqual(ModelPlanItemStates.Done, reopened.State);
            // The line that was DROPPED was never applied, so it was never undone either: the gap it named
            // is exactly where it was.
            Assert.Contains(dropped.Target.Split('/').Last(), string.Join(" ", reopened.Diff) + " " + reopened.Sub,
                StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Approve_closes_the_gap_and_the_item_reads_done_on_the_next_compose()
        {
            using var f = await GappyAsync("ChangeDone.bim");
            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());
            var proposed = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");
            var set = Row(proposed.Plan, row.Id).ChangeSet;

            var result = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "human", set.Digest);
            Assert.True(result.Performed);

            var after = Row(result.Plan, row.Id);
            // Either the gap is gone and the row reads done, or it survived and the row says applied. Both
            // are honest and the pair is the whole point: nothing stamps done, the absence of the gap does.
            Assert.NotNull(after);
            Assert.Contains(after.State, new[] { ModelPlanItemStates.Done, ModelPlanItemStates.Applied });
            if (after.State == ModelPlanItemStates.Done)
                Assert.Equal(ModelPlanStatuses.Done, after.Status);
        }

        [Fact]
        public async Task Approve_refuses_when_the_model_moved_and_applies_nothing()
        {
            using var f = await GappyAsync("ChangeMoved.bim");
            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());
            var proposed = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");
            var digest = Row(proposed.Plan, row.Id).ChangeSet.Digest;

            // Somebody else edits the model between the proposal and the approval.
            await f.Engine.CreateMeasureAsync("table:Sales", "Another", "1", "human");

            var stale = Row(await f.Engine.GetModelPlanAsync(), row.Id);
            Assert.True(stale.ChangeSet.Stale);
            Assert.Contains("out of date", stale.ChangeSet.StaleReason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("work them out again", stale.ChangeSet.Sentence, StringComparison.OrdinalIgnoreCase);

            var result = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "human", digest);

            Assert.False(result.Performed);
            Assert.Contains("Nothing was applied", result.Sentence, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Approve_refuses_a_digest_that_is_not_the_one_on_the_plan()
        {
            using var f = await GappyAsync("ChangeDigest.bim");
            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());
            await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");

            var result = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "human", "not-the-digest");

            Assert.False(result.Performed);
            Assert.Contains("not the ones you approved", result.Sentence, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Approve_with_no_digest_refuses_rather_than_helping()
        {
            using var f = await GappyAsync("ChangeNoDigest.bim");
            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());
            await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");

            var result = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "human", null);

            Assert.False(result.Performed);
            Assert.Contains("Name the exact set", result.Sentence, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Approve_with_nothing_proposed_applies_nothing()
        {
            using var f = await GappyAsync("ChangeNothing.bim");
            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());

            var result = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "human", "anything");

            Assert.False(result.Performed);
            Assert.Contains("No changes have been worked out", result.Sentence, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Approving_the_same_digest_twice_applies_it_once()
        {
            using var f = await GappyAsync("ChangeTwice.bim");
            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());
            var proposed = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");
            var digest = Row(proposed.Plan, row.Id).ChangeSet.Digest;

            var first = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "human", digest);
            Assert.True(first.Performed);
            var revisionAfterFirst = first.Revision;

            var second = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "human", digest);

            // The honest answer to "did my change land?" is yes, not a refusal about a model that has moved.
            Assert.False(second.Performed);
            // Either "already in" (the set is still on the row) or "already done" (the gap went with it).
            // Both are the same honest answer arriving by different roads; neither is a refusal about a
            // revision that moved, which is what this check exists to prevent.
            Assert.Contains("already", second.Sentence, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("second time", second.Sentence, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(revisionAfterFirst, second.Revision);
        }

        [Fact]
        public async Task A_kept_line_that_cannot_be_made_puts_the_whole_set_back()
        {
            using var f = await GappyAsync("ChangeRollback.bim");
            // The Formatting row carries the data-category fix, whose value can be made invalid through the
            // plan alone. That matters: breaking a line by editing the MODEL would move the revision and the
            // set would be refused as out of date before an apply was ever attempted.
            var row = (await f.Engine.GetModelPlanAsync()).Items
                .First(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal)
                            && i.Id.IndexOf("Format", StringComparison.OrdinalIgnoreCase) >= 0);
            var proposed = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");
            var set = Row(proposed.Plan, row.Id).ChangeSet;
            var poison = set.Lines.First(l => string.Equals(l.Op, "set_summarize_by", StringComparison.OrdinalIgnoreCase));
            Assert.True(set.Lines.Length >= 2, "the rollback has to have something else to put back");
            var survivor = set.Lines.First(l => l.Id != poison.Id);

            // "NotAnAggregate" is not a member of the aggregate enum, so applying this line throws. Poisoning
            // the PLAN rather than the model matters: editing the model would move the revision and the set
            // would be refused as out of date long before an apply was attempted.
            await f.Engine.SetPlanItemAsync(poison.PlanItemId, "NotAnAggregate", approved: true, "human");
            var ready = Row(await f.Engine.GetModelPlanAsync(), row.Id).ChangeSet;
            var revisionBefore = ready.Revision;

            var result = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "human", ready.Digest);

            Assert.False(result.Performed);
            Assert.Contains("whole set was put back", result.Sentence, StringComparison.OrdinalIgnoreCase);

            // NOTHING WAS LEFT HALF APPLIED. Working the changes out again is the proof: the survivor's gap
            // is still there, with the same before and the same after, so its line was never written.
            var again = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");
            var reworked = Row(again.Plan, row.Id).ChangeSet;
            var same = reworked.Lines.SingleOrDefault(l => l.Id == survivor.Id);
            Assert.NotNull(same);
            Assert.Equal(survivor.Before, same.Before);
            Assert.Equal(survivor.After, same.After);
            Assert.True(revisionBefore <= again.Plan.Revision);
        }

        [Fact]
        public async Task Approve_refuses_when_the_file_on_disk_moved_under_the_proposal()
        {
            using var f = await GappyAsync("ChangeSourceMoved.bim");
            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());
            var proposed = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");
            var digest = Row(proposed.Plan, row.Id).ChangeSet.Digest;
            var revisionBefore = proposed.Plan.Revision;

            // Somebody edits the model's FILE while it is open. The revision does not move, because the
            // revision counts what this session did, so the source check is the only thing that can catch it.
            var file = System.IO.Path.Combine(f.Workspace, "ChangeSourceMoved.bim");
            System.IO.File.AppendAllText(file, "\n");

            var stale = Row(await f.Engine.GetModelPlanAsync(), row.Id);
            Assert.Equal(revisionBefore, stale.ChangeSet.Revision);   // the model itself did not move
            Assert.True(stale.ChangeSet.Stale);
            Assert.Contains("file on disk", stale.ChangeSet.StaleReason, StringComparison.OrdinalIgnoreCase);

            var result = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "human", digest);

            Assert.False(result.Performed);
            Assert.Contains("Nothing was applied", result.Sentence, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Approve_says_the_way_back_out_loud_and_it_is_one_undo_step()
        {
            using var f = await GappyAsync("ChangePromise.bim");
            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());
            var proposed = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");
            var digest = Row(proposed.Plan, row.Id).ChangeSet.Digest;

            var result = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "human", digest);

            // The promise is the session undo timeline and the sentence says so in those words. It must
            // never imply a durable checkpoint, because approve does not make one.
            Assert.Contains("Undo puts", result.Sentence, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("checkpoint", result.Sentence, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("commit", result.Sentence, StringComparison.OrdinalIgnoreCase);

            var landed = Row(result.Plan, row.Id);
            if (landed != null && landed.ChangeSet != null)
                Assert.StartsWith("undo:", landed.ChangeSet.Checkpoint, StringComparison.Ordinal);
        }

        // ---- the digest names one model in one session ------------------------------------------------

        [Fact]
        public void The_digest_covers_which_model_and_which_job_and_not_only_the_content()
        {
            // The precise half of the same finding, held where it can only be about one thing. Two sets with
            // IDENTICAL content and identical versions, differing ONLY in which job and which world they
            // belong to, must not hash alike. Before this, the digest covered the items, the revision, the
            // source and the catalogue, and not even the job id, so content alone decided it.
            var item = new Semanticus.Engine.Jev.ChangeItem(
                "setProperty", "column:Sales/CustomerKey", "false", "true",
                new Semanticus.Engine.Jev.ChangeProvenance(Semanticus.Engine.Jev.ChangeItemKind.Rule, rule: "VIS-FK"));

            var one = new Semanticus.Engine.Jev.ChangeSet("job-1", 41, "src-1", "", new[] { item },
                                                          scope: "disk:aaaaaaaa\u001dsession-A");
            var sameEverythingElse = new Semanticus.Engine.Jev.ChangeSet("job-1", 41, "src-1", "", new[] { item },
                                                          scope: "disk:bbbbbbbb\u001dsession-B");
            var sameWorldOtherJob = new Semanticus.Engine.Jev.ChangeSet("job-2", 41, "src-1", "", new[] { item },
                                                          scope: "disk:aaaaaaaa\u001dsession-A");

            Assert.NotEqual(one.Digest, sameEverythingElse.Digest);
            Assert.NotEqual(one.Digest, sameWorldOtherJob.Digest);

            // And it is still a pure function of what it covers: the same set twice is the same digest, or
            // nobody could quote one back.
            var again = new Semanticus.Engine.Jev.ChangeSet("job-1", 41, "src-1", "", new[] { item },
                                                          scope: "disk:aaaaaaaa\u001dsession-A");
            Assert.Equal(one.Digest, again.Digest);
        }

        [Fact]
        public async Task A_digest_from_one_model_cannot_approve_the_work_on_a_copy_of_it()
        {
            using var f = await GappyAsync("DigestA.bim");
            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());
            var a = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");
            var digestA = Row(a.Plan, row.Id).ChangeSet.Digest;

            // A COPY, byte for byte, opened at the same revision and proposed the same way. Everything the
            // digest used to cover is identical across the two: the same items, the same revision, the same
            // file length and write time. Only the model's own identity and the session tell them apart, and
            // the digest has to carry both or it is a fact about content and not about a decision.
            var copy = System.IO.Path.Combine(f.Workspace, "DigestB.bim");
            System.IO.File.Copy(System.IO.Path.Combine(f.Workspace, "DigestA.bim"), copy);
            System.IO.File.SetLastWriteTimeUtc(copy,
                System.IO.File.GetLastWriteTimeUtc(System.IO.Path.Combine(f.Workspace, "DigestA.bim")));
            await f.Engine.OpenAsync(copy, discardUnsaved: true);

            var rowB = SafeFixRow(await f.Engine.GetModelPlanAsync());
            Assert.Equal(row.Id, rowB.Id);
            var b = await f.Engine.ModelPlanActionAsync(rowB.Id, "propose", "human");
            var setB = Row(b.Plan, rowB.Id).ChangeSet;

            Assert.NotEqual(digestA, setB.Digest);

            var result = await f.Engine.ModelPlanActionAsync(rowB.Id, "approve", "human", digestA);
            Assert.False(result.Performed);
            Assert.Contains("not the ones you approved", result.Sentence, StringComparison.OrdinalIgnoreCase);
        }

        // ---- the approval covers the write, and its rollback is its own -------------------------------

        [Fact]
        public async Task A_failed_approval_leaves_an_unrelated_edit_alone()
        {
            using var f = await GappyAsync("FixUndoScope.bim");

            // An edit that has nothing to do with the approval, made BEFORE the proposal so the proposal is
            // current. This is the thing a blind undo used to eat.
            await f.Engine.CreateMeasureAsync("table:Sales", "Canary", "1", "human");
            Assert.Contains(await f.Engine.ListMeasuresAsync(), m => m.Name == "Canary");

            var row = (await f.Engine.GetModelPlanAsync()).Items
                .First(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal)
                            && i.Id.IndexOf("Format", StringComparison.OrdinalIgnoreCase) >= 0);
            var proposed = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");
            var set = Row(proposed.Plan, row.Id).ChangeSet;
            var poison = set.Lines.First(l => string.Equals(l.Op, "set_summarize_by", StringComparison.OrdinalIgnoreCase));

            // Exactly ONE kept line, and it fails on an enum that does not parse, so the batch writes
            // NOTHING at all. An empty batch leaves no undo entry, which is what made a blind undo reach
            // past this approval and take the Canary with it.
            foreach (var line in set.Lines.Where(l => l.Id != poison.Id && l.Kept))
                await f.Engine.SetPlanItemAsync(line.PlanItemId, null, approved: false, "human");
            await f.Engine.SetPlanItemAsync(poison.PlanItemId, "NotAnAggregate", approved: true, "human");

            var ready = Row(await f.Engine.GetModelPlanAsync(), row.Id).ChangeSet;
            Assert.Equal(1, ready.KeptCount);

            var result = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "human", ready.Digest);

            Assert.False(result.Performed);
            Assert.Contains("Nothing was changed", result.Sentence, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(await f.Engine.ListMeasuresAsync(), m => m.Name == "Canary");
        }

        [Fact]
        public async Task A_value_changed_after_the_digest_was_read_makes_approve_refuse()
        {
            using var f = await GappyAsync("FixWriteBinding.bim");
            await f.Engine.AddPlanItemAsync("measure:Sales/Total Sales", "set_description",
                "What the person read.", null, null, null, "agent");

            var describe = Row(await f.Engine.GetModelPlanAsync(), "words/model");
            Assert.NotNull(describe);
            var line = describe.ChangeSet.Lines.Single(l => string.Equals(l.Op, "set_description", StringComparison.OrdinalIgnoreCase));
            await f.Engine.SetPlanItemAsync(line.PlanItemId, "What the person read.", approved: true, "human");
            var ready = Row(await f.Engine.GetModelPlanAsync(), "words/model").ChangeSet;

            // The other door re-authors the value AFTER every check approve can make before the write. Only
            // a check that rides into the transaction can catch this, which is the whole point of the guard.
            f.Engine.ApprovalQueuedForTest = async () =>
                await f.Engine.SetPlanItemAsync(line.PlanItemId, "Something nobody approved.", approved: true, "human");

            var result = await f.Engine.ModelPlanActionAsync("words/model", "approve", "human", ready.Digest);

            Assert.False(result.Performed);
            Assert.Contains("changed after you approved it", result.Sentence, StringComparison.OrdinalIgnoreCase);
            // And the value nobody approved was not written either.
            var wrote = await f.Engine.GetObjectAsync("measure:Sales/Total Sales");
            wrote.Properties.TryGetValue("description", out var description);
            Assert.DoesNotContain("nobody approved", (description ?? "").ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task An_id_the_next_plan_reuses_cannot_authorise_a_change_nobody_reviewed()
        {
            using var f = await GappyAsync("FixIdReuse.bim");
            await f.Engine.AddPlanItemAsync("measure:Sales/Total Sales", "set_description",
                "What the person read.", null, null, null, "agent");

            var describe = Row(await f.Engine.GetModelPlanAsync(), "words/model");
            Assert.NotNull(describe);
            var line = describe.ChangeSet.Lines.Single(l => string.Equals(l.Op, "set_description", StringComparison.OrdinalIgnoreCase));
            await f.Engine.SetPlanItemAsync(line.PlanItemId, "What the person read.", approved: true, "human");
            var ready = Row(await f.Engine.GetModelPlanAsync(), "words/model").ChangeSet;

            // THE HANDLE IS NOT THE LINE. The other door throws the whole plan away and starts a new one, so
            // the id sequence restarts and the FIRST line of the new plan wears the very handle the approval
            // quoted. Same value, same approved flag, a different object entirely. A guard that compares the
            // value and the flag alone sees nothing wrong and authorises a write to something nobody read.
            f.Engine.ApprovalQueuedForTest = async () =>
            {
                await f.Engine.ClearPlanAsync("human");
                await f.Engine.AddPlanItemAsync("measure:Sales/Old Total", "set_description",
                    "What the person read.", null, null, null, "agent");
            };

            ModelPlanActionResult result;
            try { result = await f.Engine.ModelPlanActionAsync("words/model", "approve", "human", ready.Digest); }
            finally { f.Engine.ApprovalQueuedForTest = null; }

            Assert.False(result.Performed);
            Assert.Contains("nothing was changed", result.Sentence, StringComparison.OrdinalIgnoreCase);

            // The object nobody reviewed was not written to.
            var unreviewed = await f.Engine.GetObjectAsync("measure:Sales/Old Total");
            unreviewed.Properties.TryGetValue("description", out var wroteUnreviewed);
            Assert.True(string.IsNullOrEmpty((wroteUnreviewed ?? "").ToString()),
                "a write reached an object that was never part of what was approved");

            // And neither was the one that was: the whole set is refused, not the half that still matches.
            var reviewed = await f.Engine.GetObjectAsync("measure:Sales/Total Sales");
            reviewed.Properties.TryGetValue("description", out var wroteReviewed);
            Assert.True(string.IsNullOrEmpty((wroteReviewed ?? "").ToString()));
        }

        [Fact]
        public async Task Two_approvals_of_one_digest_apply_once_and_the_receipt_stays_applied()
        {
            using var f = await GappyAsync("FixApproveTwice.bim");
            await f.Engine.AddPlanItemAsync("measure:Sales/Total Sales", "set_description",
                "Sums the amount column.", null, null, null, "agent");

            var describe = Row(await f.Engine.GetModelPlanAsync(), "words/model");
            Assert.NotNull(describe);
            var line = describe.ChangeSet.Lines.Single(l => string.Equals(l.Op, "set_description", StringComparison.OrdinalIgnoreCase));
            await f.Engine.SetPlanItemAsync(line.PlanItemId, "Sums the amount column.", approved: true, "human");
            var digest = Row(await f.Engine.GetModelPlanAsync(), "words/model").ChangeSet.Digest;

            // BOTH DOORS QUOTE THE SAME DIGEST AT ONCE. The agent's approval parks at the seam, which is
            // after it has read the set as proposed and before anything is written, so it holds exactly the
            // stale picture two simultaneous approvals hand each other. The person's approval then runs the
            // whole way through on its own, and only afterwards is the agent's let go.
            var parked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Engine.ApprovalQueuedForTest = async () =>
            {
                parked.TrySetResult(true);
                await release.Task;
            };

            ModelPlanActionResult first, second;
            var late = f.Engine.ModelPlanActionAsync("words/model", "approve", "agent", digest);
            try
            {
                await parked.Task;
                first = await f.Engine.ModelPlanActionAsync("words/model", "approve", "human", digest);
            }
            finally
            {
                release.TrySetResult(true);
                f.Engine.ApprovalQueuedForTest = null;
            }
            second = await late;

            // Exactly one of them applied, and the other says the honest thing rather than a refusal about
            // a set that could not be made.
            Assert.True(first.Performed);
            Assert.False(second.Performed);
            Assert.Contains("already in", second.Sentence, StringComparison.OrdinalIgnoreCase);

            // The receipt the first one wrote is still a receipt. The second must never have reached the
            // rollback path and stamped this set as put back, because it was not put back: it is in.
            var after = Row(await f.Engine.GetModelPlanAsync(), "words/model");
            Assert.NotNull(after?.ChangeSet);
            Assert.Equal(ModelPlanChangeStates.Applied, after.ChangeSet.State);

            // And the words the person approved are in the model, once.
            var wrote = await f.Engine.GetObjectAsync("measure:Sales/Total Sales");
            wrote.Properties.TryGetValue("description", out var description);
            Assert.Equal("Sums the amount column.", (description ?? "").ToString());
        }

        // ---- a proposal keeps the versions its content was worked out against --------------------------

        [Fact]
        public async Task A_proposal_goes_out_of_date_when_the_thing_it_describes_changes()
        {
            using var f = await GappyAsync("FixStaleAsst.bim");
            await f.Engine.AddPlanItemAsync("measure:Sales/Total Sales", "set_description",
                "Sums the amount column.", null, null, null, "agent");

            var before = Row(await f.Engine.GetModelPlanAsync(), "words/model");
            Assert.NotNull(before.ChangeSet);
            Assert.False(before.ChangeSet.Stale);
            var digest = before.ChangeSet.Digest;

            // The measure the words are ABOUT is rewritten. The description property itself has not moved,
            // so the applier's own per-value guard would let the old words through: only the proposal's own
            // collected-at revision can catch it.
            await f.Engine.SetDaxAsync("measure:Sales/Total Sales", "SUM ( Sales[Amount] ) * 2", "human");

            var after = Row(await f.Engine.GetModelPlanAsync(), "words/model");
            Assert.True(after.ChangeSet.Stale);
            Assert.Contains("out of date", after.ChangeSet.StaleReason, StringComparison.OrdinalIgnoreCase);

            var result = await f.Engine.ModelPlanActionAsync("words/model", "approve", "human", digest);
            Assert.False(result.Performed);
            Assert.Contains("Nothing was applied", result.Sentence, StringComparison.OrdinalIgnoreCase);
        }

        // ---- dropping an assistant set really drops it -------------------------------------------------

        [Fact]
        public async Task Dropping_an_assistant_set_removes_its_lines_and_retires_its_digest()
        {
            using var f = await GappyAsync("FixCancelAsst.bim");
            await f.Engine.AddPlanItemAsync("measure:Sales/Old Total", "rename", "Prior Year Total",
                null, null, null, "agent");

            var opened = Row(await f.Engine.GetModelPlanAsync(), "asst/rename");
            Assert.NotNull(opened);
            var digest = opened.ChangeSet.Digest;
            var handle = opened.ChangeSet.Lines.Single().PlanItemId;

            var cancelled = await f.Engine.ModelPlanActionAsync("asst/rename", "cancel", "human");
            Assert.False(cancelled.Performed);

            // Gone from the change plan itself, not merely from this answer.
            var flat = await f.Engine.GetPlanAsync();
            Assert.DoesNotContain(flat.Items, i => string.Equals(i.Id, handle, StringComparison.OrdinalIgnoreCase));
            Assert.Null(Row(cancelled.Plan, "asst/rename"));

            // And the row that went is NOT reported as finished. Every other row on the plan leaves because
            // its gap closed, which is what done means; an assistant's row leaves because its changes were
            // dropped, having finished nothing, and counting that as progress would be work nobody did.
            Assert.DoesNotContain(cancelled.Plan.Items, i => i.State == ModelPlanItemStates.Done
                                                             && i.Id == "asst/rename");

            // And the next compose cannot rebuild it, so the digest a person already approved is retired.
            var again = await f.Engine.GetModelPlanAsync();
            Assert.Null(Row(again, "asst/rename"));
            var later = await f.Engine.ModelPlanActionAsync("asst/rename", "approve", "human", digest);
            Assert.False(later.Performed);
        }

        // ---- a receipt is not a proposal ---------------------------------------------------------------

        [Fact]
        public async Task New_work_under_an_item_that_already_applied_is_proposed_not_applied()
        {
            using var f = await GappyAsync("FixReceipt.bim");
            await f.Engine.AddPlanItemAsync("measure:Sales/Total Sales", "set_description",
                "Sums the amount column.", null, null, null, "agent");
            var first = Row(await f.Engine.GetModelPlanAsync(), "words/model");
            var firstLine = first.ChangeSet.Lines.Single();
            await f.Engine.SetPlanItemAsync(firstLine.PlanItemId, null, approved: true, "human");
            var ready = Row(await f.Engine.GetModelPlanAsync(), "words/model").ChangeSet;

            var applied = await f.Engine.ModelPlanActionAsync("words/model", "approve", "human", ready.Digest);
            Assert.True(applied.Performed);

            // More work arrives under the SAME item. It has not run, and it must not read as though it had.
            await f.Engine.AddPlanItemAsync("measure:Sales/Old Total", "set_description",
                "The previous year's total.", null, null, null, "agent");

            var next = Row(await f.Engine.GetModelPlanAsync(), "words/model");
            Assert.NotNull(next);
            Assert.Equal(ModelPlanItemStates.Proposed, next.State);
            Assert.Equal(ModelPlanChangeStates.Proposed, next.ChangeSet.State);
            Assert.Single(next.ChangeSet.Lines);
            // The earlier run is carried BESIDE it rather than folded into it.
            Assert.NotNull(next.ChangeSet.LastAppliedAt);

            await f.Engine.SetPlanItemAsync(next.ChangeSet.Lines[0].PlanItemId, null, approved: true, "human");
            var second = Row(await f.Engine.GetModelPlanAsync(), "words/model").ChangeSet;
            var result = await f.Engine.ModelPlanActionAsync("words/model", "approve", "human", second.Digest);
            Assert.True(result.Performed);
        }

        // ---- a detached snapshot decides nothing -------------------------------------------------------

        [Fact]
        public async Task A_snapshot_of_lines_that_are_gone_carries_no_handles()
        {
            using var f = await GappyAsync("FixDetached.bim");
            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());
            var proposed = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");
            Assert.Contains(Row(proposed.Plan, row.Id).ChangeSet.Lines, l => l.PlanItemId == "ci-0001");

            // The whole change plan goes, then one unrelated change arrives. The new list starts its
            // numbering again, so "ci-0001" now names somebody else's rename.
            await f.Engine.ClearPlanAsync("human");
            await f.Engine.AddPlanItemAsync("measure:Sales/Old Total", "rename", "Prior Year Total",
                null, null, null, "agent");
            var reused = (await f.Engine.GetPlanAsync()).Items.Single();
            Assert.Equal("ci-0001", reused.Id);

            // The old set is still readable, because a person may want to see what it said, and it carries
            // NO handles, so nothing on it can decide the rename that now answers to that id.
            var stale = Row(await f.Engine.GetModelPlanAsync(), row.Id);
            Assert.NotNull(stale.ChangeSet);
            Assert.NotEmpty(stale.ChangeSet.Lines);
            Assert.All(stale.ChangeSet.Lines, l => Assert.True(string.IsNullOrEmpty(l.PlanItemId)));
            Assert.True(stale.ChangeSet.Stale);
        }

        // ---- cancel -----------------------------------------------------------------------------------

        [Fact]
        public async Task Cancel_drops_the_set_and_changes_nothing()
        {
            using var f = await GappyAsync("ChangeCancel.bim");
            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());
            var proposed = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");
            var digest = Row(proposed.Plan, row.Id).ChangeSet.Digest;
            var revisionBefore = proposed.Plan.Revision;

            var result = await f.Engine.ModelPlanActionAsync(row.Id, "cancel", "human");

            Assert.False(result.Performed);
            Assert.Contains("Nothing was changed", result.Sentence, StringComparison.OrdinalIgnoreCase);
            var after = Row(result.Plan, row.Id);
            Assert.Equal(ModelPlanItemStates.Gap, after.State);
            Assert.Null(after.ChangeSet);
            Assert.Equal(revisionBefore, result.Revision);

            // And the digest can never be approved afterwards.
            var later = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "human", digest);
            Assert.False(later.Performed);
        }

        [Fact]
        public async Task Cancelling_something_already_applied_says_to_undo_it_instead()
        {
            using var f = await GappyAsync("ChangeCancelApplied.bim");
            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());
            var proposed = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");
            var digest = Row(proposed.Plan, row.Id).ChangeSet.Digest;
            var applied = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "human", digest);
            Assert.True(applied.Performed);

            var still = Row(applied.Plan, row.Id);
            if (still == null || still.State != ModelPlanItemStates.Applied) return;   // the gap closed; nothing to cancel

            var result = await f.Engine.ModelPlanActionAsync(row.Id, "cancel", "human");
            Assert.False(result.Performed);
            Assert.Contains("Undo them instead", result.Sentence, StringComparison.OrdinalIgnoreCase);
        }

        // ---- the assistant's own proposals -------------------------------------------------------------

        [Fact]
        public async Task An_assistant_proposal_lands_on_the_item_it_serves()
        {
            using var f = await GappyAsync("ChangeAsstAttach.bim");
            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());

            // The assistant's whole-model proposal, through the door it already has. Nothing about the model
            // plan is mentioned; the lines land on its items because of what they ARE.
            await f.Engine.ProposePlanAsync(null, includeAi: false, maxAiItems: 0, "agent");

            var after = Row(await f.Engine.GetModelPlanAsync(), row.Id);
            Assert.NotNull(after.ChangeSet);
            Assert.NotEmpty(after.ChangeSet.Lines);
            Assert.Equal(ModelPlanItemStates.Proposed, after.State);
        }

        [Fact]
        public async Task An_assistant_change_no_item_claims_opens_an_item_of_its_own()
        {
            using var f = await GappyAsync("ChangeAsstNew.bim");
            var before = await f.Engine.GetModelPlanAsync();
            Assert.DoesNotContain(before.Items, i => i.Id.StartsWith("asst/", StringComparison.Ordinal));

            // A rename serves no plan item: nothing on the plan is about renaming this measure.
            await f.Engine.AddPlanItemAsync("measure:Sales/Old Total", "rename", "Prior Year Total",
                null, null, null, "agent");

            var after = await f.Engine.GetModelPlanAsync();
            var opened = after.Items.FirstOrDefault(i => string.Equals(i.Id, "asst/rename", StringComparison.Ordinal));
            Assert.NotNull(opened);
            Assert.Equal(ModelPlanOwners.Assistant, opened.Who);
            Assert.Equal(ModelPlanItemStates.Proposed, opened.State);
            Assert.NotNull(opened.ChangeSet);
            Assert.Single(opened.ChangeSet.Lines);
            // A rename is opt-in and arrives DROPPED, so nothing is pre-approved on a person's behalf.
            Assert.False(opened.ChangeSet.Lines[0].Kept);
            Assert.NotNull(AskRouteCatalog.Find(opened.Jump.Tool));
        }

        // ---- free and Pro ------------------------------------------------------------------------------

        [Fact]
        public async Task Free_can_propose_keep_drop_and_cancel()
        {
            using var f = await GappyAsync("ChangeFree.bim", pro: false);
            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());

            var proposed = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");
            var set = Row(proposed.Plan, row.Id).ChangeSet;
            Assert.NotNull(set);
            Assert.NotEmpty(set.Lines);

            await f.Engine.SetPlanItemAsync(set.Lines[0].PlanItemId, null, approved: false, "human");
            Assert.False(Row(await f.Engine.GetModelPlanAsync(), row.Id).ChangeSet.Lines
                .Single(l => l.Id == set.Lines[0].Id).Kept);

            var cancelled = await f.Engine.ModelPlanActionAsync(row.Id, "cancel", "human");
            Assert.Null(Row(cancelled.Plan, row.Id).ChangeSet);
        }

        [Fact]
        public async Task Only_pro_can_approve_a_row_the_engine_runs_for_you()
        {
            using var f = await GappyAsync("ChangeFreeApprove.bim", pro: false);
            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());
            Assert.Equal(ModelPlanOwners.Auto, row.Who);
            var proposed = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");
            var digest = Row(proposed.Plan, row.Id).ChangeSet.Digest;
            var revisionBefore = proposed.Plan.Revision;

            var result = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "human", digest);

            Assert.False(result.Performed);
            Assert.Equal(LocalEngine.RunIsProSentence, result.Sentence);
            Assert.Equal(revisionBefore, result.Revision);
        }

        [Fact]
        public async Task Free_can_approve_a_row_that_was_always_yours_to_do()
        {
            using var f = await GappyAsync("ChangeFreeYours.bim", pro: false);
            await PlanHarmKit.AsExistingModelWithAReportReadAsync(f, "ChangeFreeYours.bim");
            var cleanup = Row(await f.Engine.GetModelPlanAsync(), "cleanup/model");
            Assert.NotNull(cleanup);
            Assert.Equal(ModelPlanOwners.You, cleanup.Who);

            var proposed = await f.Engine.ModelPlanActionAsync("cleanup/model", "propose", "human");
            var set = Row(proposed.Plan, "cleanup/model").ChangeSet;
            Assert.NotNull(set);
            Assert.NotEmpty(set.Lines);
            // Round 5: a line the rule suggests arrives ticked (and marked suggested), every other line arrives dropped, and either way
            // it is the person's Approve that removes anything.
            Assert.All(set.Lines, l => Assert.Equal(l.Kept, l.Suggested));

            await f.Engine.SetPlanItemAsync(set.Lines[0].PlanItemId, null, approved: true, "human");
            var ready = Row(await f.Engine.GetModelPlanAsync(), "cleanup/model").ChangeSet;

            var result = await f.Engine.ModelPlanActionAsync("cleanup/model", "approve", "human", ready.Digest);

            Assert.NotEqual(LocalEngine.RunIsProSentence, result.Sentence);
            Assert.True(result.Performed);
        }

        // ---- the refusals that are most of the behaviour ----------------------------------------------

        [Fact]
        public async Task Proposing_on_a_words_item_hands_off_to_the_assistant()
        {
            using var f = await GappyAsync("ChangeWords.bim");
            var describe = Row(await f.Engine.GetModelPlanAsync(), "words/model");
            Assert.NotNull(describe);
            Assert.Equal(ModelPlanOwners.Assistant, describe.Who);

            var result = await f.Engine.ModelPlanActionAsync("words/model", "propose", "human");

            Assert.False(result.Performed);
            Assert.Contains("your assistant writes them", result.Sentence, StringComparison.OrdinalIgnoreCase);
            Assert.Null(Row(result.Plan, "words/model").ChangeSet);
        }

        [Fact]
        public async Task Proposing_on_a_set_aside_item_refuses_and_says_to_put_it_back()
        {
            using var f = await GappyAsync("ChangeSkipped.bim");
            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());
            await f.Engine.SkipModelPlanItemAsync(row.Id, "not now", "human");

            var result = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");

            Assert.False(result.Performed);
            Assert.Contains("set aside", result.Sentence, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task An_unknown_action_names_all_four_verbs()
        {
            using var f = await GappyAsync("ChangeVerb.bim");
            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());

            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => f.Engine.ModelPlanActionAsync(row.Id, "explode", "human"));

            Assert.Contains("propose", ex.Message, StringComparison.Ordinal);
            Assert.Contains("approve", ex.Message, StringComparison.Ordinal);
            Assert.Contains("cancel", ex.Message, StringComparison.Ordinal);
            Assert.Contains("start", ex.Message, StringComparison.Ordinal);
        }

        // ---- the story ---------------------------------------------------------------------------------

        [Fact]
        public async Task The_history_records_proposed_approved_and_cancelled()
        {
            using var f = await GappyAsync("ChangeHistory.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var row = SafeFixRow(plan);
            var other = plan.Items.First(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal) && i.Id != row.Id);

            var proposed = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");
            Assert.Contains(proposed.Plan.History, h => h.Kind == ModelPlanHistoryKinds.Proposed && h.ItemId == row.Id);

            var digest = Row(proposed.Plan, row.Id).ChangeSet.Digest;
            var approved = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "human", digest);
            var entry = approved.Plan.History.Last(h => h.Kind == ModelPlanHistoryKinds.Approved);
            Assert.Equal(row.Id, entry.ItemId);
            Assert.NotEmpty(entry.Detail);   // the lines that actually went in

            await f.Engine.ModelPlanActionAsync(other.Id, "propose", "human");
            var cancelled = await f.Engine.ModelPlanActionAsync(other.Id, "cancel", "human");
            Assert.Contains(cancelled.Plan.History, h => h.Kind == ModelPlanHistoryKinds.Cancelled && h.ItemId == other.Id);
        }

        [Fact]
        public async Task Proposing_does_not_make_the_plan_gather_again()
        {
            using var f = await GappyAsync("ChangeCache.bim");
            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());
            var gathers = f.Engine.PlanGatherCountForTest;

            await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");
            await f.Engine.GetModelPlanAsync();

            // A proposal does not move the revision, so the remembered facts stay valid and nothing expensive
            // runs again. The plan is recomposed, which is arithmetic.
            Assert.Equal(gathers, f.Engine.PlanGatherCountForTest);
        }

        // ---- the model must not move underneath a verb's write (Astra 6 on the Fix slice) ---------------
        //
        // Each verb reads the open session at its top, works for a while, and only then records what it did.
        // Between those two moments the open model can be exchanged. These three prove the record lands on
        // neither model rather than on the other one, and that the caller is told in a sentence.

        /// <summary>A SECOND model saved in the SAME folder, so both resolve to one plan-memory.json and the
        /// only thing keeping them apart is the identity a write is keyed by. That is the seam under test.
        /// Returns the second model's path, with the first left open and ready to act on.</summary>
        private static async Task<string> SecondModelInTheSameFolderAsync(
            DecisionFixture f, string firstPath, string secondFile)
        {
            await f.Engine.CreateModelAsync("Contoso Two", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.SaveAsAsync(secondFile);
            var secondPath = Path.Combine(f.Workspace, secondFile);

            await f.Engine.OpenAsync(firstPath);
            f.UseNull();
            return secondPath;
        }

        [Fact]
        public async Task Propose_records_itself_on_neither_model_when_the_open_model_is_exchanged()
        {
            using var f = await GappyAsync("VerbRaceProposeA.bim");
            var aPath = Path.Combine(f.Workspace, "VerbRaceProposeA.bim");
            var bPath = await SecondModelInTheSameFolderAsync(f, aPath, "VerbRaceProposeB.bim");
            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());

            var swapped = false;
            f.Engine.PlanVerbWriteForTest = async () =>
            {
                swapped = true;
                await f.Engine.OpenAsync(bPath);
                f.UseNull();
            };

            ModelPlanActionResult result;
            try { result = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human"); }
            finally { f.Engine.PlanVerbWriteForTest = null; }

            Assert.True(swapped, "the seam never fired, so this test proved nothing");
            Assert.False(result.Performed);
            Assert.Equal(LocalEngine.PlanMemoryModelSwitchedSentence, result.Sentence);
            Assert.DoesNotContain("\u2014", result.Sentence, StringComparison.Ordinal);

            // B was the open model when the record would have been written. B proposed nothing.
            var onB = await f.Engine.GetModelPlanAsync();
            Assert.DoesNotContain(onB.History, h => h.Kind == ModelPlanHistoryKinds.Proposed);
            Assert.All(onB.Items, i => Assert.Null(i.ChangeSet));
        }

        [Fact]
        public async Task Approve_applies_nothing_and_records_nothing_when_the_open_model_is_exchanged()
        {
            using var f = await GappyAsync("VerbRaceApproveA.bim");
            var aPath = Path.Combine(f.Workspace, "VerbRaceApproveA.bim");
            var bPath = await SecondModelInTheSameFolderAsync(f, aPath, "VerbRaceApproveB.bim");

            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());
            var proposed = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");
            var digest = Row(proposed.Plan, row.Id).ChangeSet.Digest;

            // The exchange happens at the exact moment approve has finished every check and is about to queue
            // the batch, which is the last instant at which nothing has been written yet.
            var swapped = false;
            f.Engine.ApprovalQueuedForTest = async () =>
            {
                swapped = true;
                await f.Engine.OpenAsync(bPath);
                f.UseNull();
            };

            ModelPlanActionResult result;
            try { result = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "human", digest); }
            finally { f.Engine.ApprovalQueuedForTest = null; }

            Assert.True(swapped, "the seam never fired, so this test proved nothing");
            Assert.False(result.Performed);
            Assert.Equal(LocalEngine.PlanMemoryModelSwitchedSentence, result.Sentence);
            // Nothing was applied to either model. The revision is the whole of that claim: a write would
            // have moved it.
            Assert.Equal(proposed.Plan.Revision, result.Revision);

            // B was the open model when the write would have happened, and nothing of A's reached it: no
            // approval, no rollback and no set. (B's own history is not empty: reading a plan writes that
            // model's refresh note, which is the read path's own bookkeeping and says nothing about A.)
            var onB = await f.Engine.GetModelPlanAsync();
            Assert.DoesNotContain(onB.History, h => h.Kind == ModelPlanHistoryKinds.Approved);
            Assert.DoesNotContain(onB.History, h => h.Kind == ModelPlanHistoryKinds.Refused);
            Assert.All(onB.Items, i => Assert.Null(i.ChangeSet));
        }

        [Fact]
        public async Task Cancel_records_itself_on_neither_model_when_the_open_model_is_exchanged()
        {
            using var f = await GappyAsync("VerbRaceCancelA.bim");
            var aPath = Path.Combine(f.Workspace, "VerbRaceCancelA.bim");
            var bPath = await SecondModelInTheSameFolderAsync(f, aPath, "VerbRaceCancelB.bim");

            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());
            await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human");

            var swapped = false;
            f.Engine.PlanVerbWriteForTest = async () =>
            {
                swapped = true;
                await f.Engine.OpenAsync(bPath);
                f.UseNull();
            };

            ModelPlanActionResult result;
            try { result = await f.Engine.ModelPlanActionAsync(row.Id, "cancel", "human"); }
            finally { f.Engine.PlanVerbWriteForTest = null; }

            Assert.True(swapped, "the seam never fired, so this test proved nothing");
            Assert.False(result.Performed);
            Assert.Equal(LocalEngine.PlanMemoryModelSwitchedSentence, result.Sentence);

            // B never had a set on this row, so a drop recorded against B would be a drop of nothing, filed
            // under a model nobody asked about.
            var onB = await f.Engine.GetModelPlanAsync();
            Assert.DoesNotContain(onB.History, h => h.Kind == ModelPlanHistoryKinds.Cancelled);
        }

        // ---- a REFUSAL lands on neither model either (F-227) --------------------------------------------
        //
        // The three above prove a verb's success record binds to the model the verb started on. A refusal
        // had the same window and was not covered: the verb bound at its top, decided to refuse, and the
        // refusal composed with a binding of its own taken at that later moment. So model A's item and
        // model A's reason were written into model B's plan memory, under A's item id, for a verb that ran
        // against A. These three stand in exactly that window, one per verb.

        /// <summary>Swap to the second model at the instant the verb has decided to refuse. Returns whether
        /// the seam actually fired, because a test that proves nothing must say so rather than pass.</summary>
        private static Func<Task> SwapAtRefusal(DecisionFixture f, string toPath, Action onSwap) => async () =>
        {
            onSwap();
            await f.Engine.OpenAsync(toPath);
            f.UseNull();
        };

        [Fact]
        public async Task Propose_records_its_refusal_on_neither_model_when_the_open_model_is_exchanged()
        {
            using var f = await GappyAsync("VerbRefuseProposeA.bim");
            var aPath = Path.Combine(f.Workspace, "VerbRefuseProposeA.bim");
            var bPath = await SecondModelInTheSameFolderAsync(f, aPath, "VerbRefuseProposeB.bim");

            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());
            await f.Engine.SkipModelPlanItemAsync(row.Id, "Not this week.", "human");

            var swapped = false;
            f.Engine.PlanRefusalWriteForTest = SwapAtRefusal(f, bPath, () => swapped = true);

            ModelPlanActionResult result;
            try { result = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human"); }
            finally { f.Engine.PlanRefusalWriteForTest = null; }

            Assert.True(swapped, "the seam never fired, so this test proved nothing");
            Assert.False(result.Performed);
            Assert.Contains("set aside", result.Sentence, StringComparison.OrdinalIgnoreCase);

            var onB = await f.Engine.GetModelPlanAsync();
            Assert.DoesNotContain(onB.History, h => h.Kind == ModelPlanHistoryKinds.Refused);
        }

        [Fact]
        public async Task Approve_records_its_refusal_on_neither_model_when_the_open_model_is_exchanged()
        {
            using var f = await GappyAsync("VerbRefuseApproveA.bim");
            var aPath = Path.Combine(f.Workspace, "VerbRefuseApproveA.bim");
            var bPath = await SecondModelInTheSameFolderAsync(f, aPath, "VerbRefuseApproveB.bim");

            // Nothing was ever worked out for this row, so approve refuses before it reaches a write.
            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());

            var swapped = false;
            f.Engine.PlanRefusalWriteForTest = SwapAtRefusal(f, bPath, () => swapped = true);

            ModelPlanActionResult result;
            try { result = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "human", "whatever"); }
            finally { f.Engine.PlanRefusalWriteForTest = null; }

            Assert.True(swapped, "the seam never fired, so this test proved nothing");
            Assert.False(result.Performed);
            Assert.Contains("No changes have been worked out", result.Sentence, StringComparison.OrdinalIgnoreCase);

            var onB = await f.Engine.GetModelPlanAsync();
            Assert.DoesNotContain(onB.History, h => h.Kind == ModelPlanHistoryKinds.Refused);
        }

        [Fact]
        public async Task Cancel_records_its_refusal_on_neither_model_when_the_open_model_is_exchanged()
        {
            using var f = await GappyAsync("VerbRefuseCancelA.bim");
            var aPath = Path.Combine(f.Workspace, "VerbRefuseCancelA.bim");
            var bPath = await SecondModelInTheSameFolderAsync(f, aPath, "VerbRefuseCancelB.bim");

            // Nothing is waiting on this row, so cancel refuses with nothing to drop.
            var row = SafeFixRow(await f.Engine.GetModelPlanAsync());

            var swapped = false;
            f.Engine.PlanRefusalWriteForTest = SwapAtRefusal(f, bPath, () => swapped = true);

            ModelPlanActionResult result;
            try { result = await f.Engine.ModelPlanActionAsync(row.Id, "cancel", "human"); }
            finally { f.Engine.PlanRefusalWriteForTest = null; }

            Assert.True(swapped, "the seam never fired, so this test proved nothing");
            Assert.False(result.Performed);
            Assert.Contains("nothing was dropped", result.Sentence, StringComparison.OrdinalIgnoreCase);

            var onB = await f.Engine.GetModelPlanAsync();
            Assert.DoesNotContain(onB.History, h => h.Kind == ModelPlanHistoryKinds.Refused);
        }
            // ---- one-step bulk apply (groundwork contract section 3) --------------------------------------

        private static ModelPlanItem FormattingRow(ModelPlanView plan) =>
            plan.Items.First(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal)
                                  && i.Id.IndexOf("Format", StringComparison.OrdinalIgnoreCase) >= 0);

        private static ModelPlanItem OtherSafeFixRow(ModelPlanView plan) =>
            plan.Items.First(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal)
                                  && i.Id.IndexOf("Format", StringComparison.OrdinalIgnoreCase) < 0);

        /// <summary>Propose the two safe-fix rows and hand back what the page would send: every proposed item
        /// with the digest it was shown.</summary>
        private static async Task<(string First, string Second, PlanItemDigest[] Sent, ModelPlanView Plan)> TwoProposedAsync(DecisionFixture f)
        {
            var plan = await f.Engine.GetModelPlanAsync();
            var first = OtherSafeFixRow(plan).Id;
            var second = FormattingRow(plan).Id;
            await f.Engine.ModelPlanActionAsync(first, "propose", "human");
            await f.Engine.ModelPlanActionAsync(second, "propose", "human");
            var ready = await f.Engine.GetModelPlanAsync();
            var sent = new[] { first, second }
                .Select(id => new PlanItemDigest { Id = id, Digest = Row(ready, id).ChangeSet.Digest })
                .ToArray();
            return (first, second, sent, ready);
        }

        [Fact]
        public async Task Bulk_apply_makes_two_items_in_one_step_and_one_undo_puts_both_back()
        {
            using var f = await GappyAsync("BulkTwo.bim");
            var (first, second, sent, ready) = await TwoProposedAsync(f);
            var kept = Row(ready, first).ChangeSet.KeptCount + Row(ready, second).ChangeSet.KeptCount;
            var revisionBefore = f.Sessions.Current.Revision;

            var result = await f.Engine.ApplyPlanItemsAsync(sent, "human");

            Assert.True(result.Performed, result.Sentence);
            Assert.True(kept == result.Applied, "applied " + result.Applied + " of " + kept + " kept");
            Assert.Equal(new[] { first, second }, result.ItemIds);
            // ONE batch: the revision moved by exactly one mutation, not one per item.
            Assert.Equal(revisionBefore + 1, result.Revision);
            Assert.Equal("Applied " + kept + " changes as one step you can undo.", result.Sentence);
            Assert.DoesNotContain(result.Plan.Items, i => (i.Id == first || i.Id == second)
                                                          && i.State == ModelPlanItemStates.Proposed);

            // A SINGLE undo puts both items' changes back, so both gaps are open again.
            await f.Engine.UndoAsync("human");
            var back = await f.Engine.GetModelPlanAsync();
            foreach (var id in new[] { first, second })
            {
                var row = Row(back, id);
                Assert.NotNull(row);
                Assert.NotEqual(ModelPlanItemStates.Done, row.State);
                // The gap is open again: working the item out again finds changes to make.
                var again = await f.Engine.ModelPlanActionAsync(id, "propose", "human");
                Assert.NotEmpty(Row(again.Plan, id).ChangeSet.Lines);
            }
        }

        [Fact]
        public async Task A_failing_line_in_the_second_item_leaves_the_first_untouched()
        {
            using var f = await GappyAsync("BulkPoison.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var first = OtherSafeFixRow(plan).Id;
            var second = FormattingRow(plan).Id;
            await f.Engine.ModelPlanActionAsync(first, "propose", "human");
            var proposed = await f.Engine.ModelPlanActionAsync(second, "propose", "human");
            // Same poison as the single-item rollback test: an enum value that is not one, set through the
            // plan so the revision does not move and the set is not refused as out of date first.
            var poison = Row(proposed.Plan, second).ChangeSet.Lines
                .First(l => string.Equals(l.Op, "set_summarize_by", StringComparison.OrdinalIgnoreCase));
            await f.Engine.SetPlanItemAsync(poison.PlanItemId, "NotAnAggregate", approved: true, "human");
            var ready = await f.Engine.GetModelPlanAsync();
            var firstSet = Row(ready, first).ChangeSet;
            var sent = new[]
            {
                new PlanItemDigest { Id = first, Digest = firstSet.Digest },
                new PlanItemDigest { Id = second, Digest = Row(ready, second).ChangeSet.Digest },
            };

            var result = await f.Engine.ApplyPlanItemsAsync(sent, "human");

            Assert.False(result.Performed);
            Assert.Equal(0, result.Applied);
            Assert.Equal(ready.Revision, result.Revision);
            Assert.Equal(second, result.FailedItemId);
            // The reply names the item and the line in one plain sentence.
            Assert.StartsWith("Nothing was changed", result.Sentence, StringComparison.Ordinal);
            Assert.Contains(Lower(Row(ready, second).Title), result.Sentence, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(poison.Target.Split('/').Last(), result.Sentence, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\u2014", result.Sentence);

            // THE FIRST ITEM IS EXACTLY AS IT WAS: still waiting, the same digest, the same lines kept.
            var after = Row(await f.Engine.GetModelPlanAsync(), first);
            Assert.Equal(ModelPlanItemStates.Proposed, after.State);
            Assert.Equal(firstSet.Digest, after.ChangeSet.Digest);
            Assert.Equal(firstSet.KeptCount, after.ChangeSet.KeptCount);

            // And it still applies on its own, which it could not if the failed batch had left its lines
            // marked applied or failed.
            var alone = await f.Engine.ApplyPlanItemsAsync(new[] { sent[0] }, "human");
            Assert.True(alone.Performed, alone.Sentence);
            Assert.Equal(firstSet.KeptCount, alone.Applied);
        }

        [Fact]
        public async Task A_stale_digest_on_one_item_refuses_the_whole_set()
        {
            using var f = await GappyAsync("BulkStale.bim");
            var (first, second, sent, ready) = await TwoProposedAsync(f);
            sent[1] = new PlanItemDigest { Id = second, Digest = "not-the-digest" };

            var result = await f.Engine.ApplyPlanItemsAsync(sent, "human");

            Assert.False(result.Performed);
            Assert.Equal(ready.Revision, result.Revision);
            Assert.Equal(second, result.FailedItemId);
            Assert.Contains("not the ones you approved", result.Sentence, StringComparison.OrdinalIgnoreCase);
            // The first item was not applied on its own either.
            Assert.Equal(ModelPlanItemStates.Proposed, Row(await f.Engine.GetModelPlanAsync(), first).State);
        }

        [Fact]
        public async Task Applying_the_same_set_twice_applies_nothing_twice()
        {
            using var f = await GappyAsync("BulkTwice.bim");
            var (_, _, sent, _) = await TwoProposedAsync(f);

            var once = await f.Engine.ApplyPlanItemsAsync(sent, "human");
            Assert.True(once.Performed, once.Sentence);

            var twice = await f.Engine.ApplyPlanItemsAsync(sent, "human");

            Assert.False(twice.Performed);
            Assert.Equal(0, twice.Applied);
            Assert.Equal(once.Revision, twice.Revision);
            Assert.Contains("second time", twice.Sentence, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Bulk_apply_on_free_refuses_a_row_the_engine_runs_for_you_and_writes_nothing()
        {
            using var f = await GappyAsync("BulkFree.bim", pro: false);
            var (_, _, sent, ready) = await TwoProposedAsync(f);

            var result = await f.Engine.ApplyPlanItemsAsync(sent, "human");

            Assert.False(result.Performed);
            Assert.Equal(ready.Revision, result.Revision);
            Assert.Equal(LocalEngine.BulkRunIsProSentence, result.Sentence);
        }

        /// <summary>A free session with the one row that is yours to do proposed and one line kept: what a
        /// free person can approve on its own.</summary>
        private static async Task<(DecisionFixture F, PlanItemDigest[] Sent, long Revision)> FreeYoursReadyAsync(string file)
        {
            var f = await GappyAsync(file, pro: false);
            await PlanHarmKit.AsExistingModelWithAReportReadAsync(f, file);
            var proposed = await f.Engine.ModelPlanActionAsync("cleanup/model", "propose", "human");
            var set = Row(proposed.Plan, "cleanup/model").ChangeSet;
            await f.Engine.SetPlanItemAsync(set.Lines[0].PlanItemId, null, approved: true, "human");
            var ready = await f.Engine.GetModelPlanAsync();
            return (f, new[] { new PlanItemDigest { Id = "cleanup/model", Digest = Row(ready, "cleanup/model").ChangeSet.Digest } },
                    ready.Revision);
        }

        // F-238: running the plan is Pro (Kane, 22 September 2026), and bulk apply is running the plan. Studio
        // only ever drew Run the plan for Pro; the engine now draws the same line on both doors, for every
        // item, so the agent door cannot do what the Studio will not offer. Free approves item by item.
        [Fact]
        public async Task Bulk_apply_on_free_is_refused_on_both_doors_even_for_a_row_that_is_yours_to_do()
        {
            var (studioFixture, studioSent, studioRevision) = await FreeYoursReadyAsync("BulkFreeYoursRpc.bim");
            using (studioFixture)
            {
                var studio = await new EngineRpcTarget(studioFixture.Engine).applyPlanItems(studioSent);   // the UI door
                Assert.False(studio.Performed);
                Assert.Equal(LocalEngine.BulkRunIsProSentence, studio.Sentence);
                Assert.StartsWith(LocalEngine.RunIsProLead, studio.Sentence, StringComparison.Ordinal);
                Assert.Equal(studioRevision, studio.Revision);
                Assert.Equal(studioRevision, studioFixture.Sessions.Current.Revision);
            }
            var (agentFixture, agentSent, agentRevision) = await FreeYoursReadyAsync("BulkFreeYoursMcp.bim");
            using (agentFixture)
            {
                var agent = await McpTools.ApplyPlanItems(agentFixture.Engine, agentSent);                 // the agent door
                Assert.False(agent.Performed);
                Assert.Equal(LocalEngine.BulkRunIsProSentence, agent.Sentence);
                Assert.Equal(agentRevision, agentFixture.Sessions.Current.Revision);
            }
        }

        [Fact]
        public async Task Free_still_approves_a_row_that_is_yours_to_do_on_its_own_on_both_doors()
        {
            var (studioFixture, studioSent, studioRevision) = await FreeYoursReadyAsync("FreeApproveRpc.bim");
            using (studioFixture)
            {
                var studio = await new EngineRpcTarget(studioFixture.Engine)
                    .modelPlanAction("cleanup/model", "approve", "human", studioSent[0].Digest);
                Assert.True(studio.Performed, studio.Sentence);
                Assert.Equal(studioRevision + 1, studioFixture.Sessions.Current.Revision);
            }
            var (agentFixture, agentSent, agentRevision) = await FreeYoursReadyAsync("FreeApproveMcp.bim");
            using (agentFixture)
            {
                var agent = await McpTools.ModelPlanAction(agentFixture.Engine, "cleanup/model", "approve", agentSent[0].Digest);
                Assert.True(agent.Performed, agent.Sentence);
                Assert.Equal(agentRevision + 1, agentFixture.Sessions.Current.Revision);
            }
        }

        [Fact]
        public async Task Bulk_apply_on_pro_applies_a_row_that_is_yours_to_do()
        {
            using var f = await GappyAsync("BulkProYours.bim");
            await PlanHarmKit.AsExistingModelWithAReportReadAsync(f, "BulkProYours.bim");
            var proposed = await f.Engine.ModelPlanActionAsync("cleanup/model", "propose", "human");
            var set = Row(proposed.Plan, "cleanup/model").ChangeSet;
            await f.Engine.SetPlanItemAsync(set.Lines[0].PlanItemId, null, approved: true, "human");
            var ready = Row(await f.Engine.GetModelPlanAsync(), "cleanup/model").ChangeSet;

            var result = await f.Engine.ApplyPlanItemsAsync(
                new[] { new PlanItemDigest { Id = "cleanup/model", Digest = ready.Digest } }, "human");

            Assert.True(result.Performed, result.Sentence);
            Assert.Equal("Applied 1 change as one step you can undo.", result.Sentence);
        }

        [Fact]
        public async Task Both_doors_reach_the_same_bulk_apply()
        {
            using (var f = await GappyAsync("BulkRpc.bim"))
            {
                var (_, _, sent, ready) = await TwoProposedAsync(f);
                var studio = await new EngineRpcTarget(f.Engine).applyPlanItems(sent);   // the UI door
                Assert.True(studio.Performed, studio.Sentence);
                Assert.Equal(ready.Revision + 1, studio.Revision);
                Assert.Contains(studio.Plan.History, h => h.Kind == ModelPlanHistoryKinds.Approved
                                                          && h.Actor == ModelPlanActors.You);
            }
            using (var f = await GappyAsync("BulkMcp.bim"))
            {
                var (_, _, sent, ready) = await TwoProposedAsync(f);
                var agent = await McpTools.ApplyPlanItems(f.Engine, sent);              // the agent door
                Assert.True(agent.Performed, agent.Sentence);
                Assert.Equal(ready.Revision + 1, agent.Revision);
                Assert.Contains(agent.History, h => h.Kind == ModelPlanHistoryKinds.Approved
                                                         && h.Actor == ModelPlanActors.Assistant);
            }
        }

        // ---- a refusal found BEFORE the write leaves every line as it was (Sol 6, finding 1) -----------

        /// <summary>An assistant item holding one DAX rewrite that uses its own measure. The circular-rewrite
        /// gate turns such a line away before any write, which is the one refusal that happens in the checks
        /// ahead of the batch rather than inside it. The line is kept, so an approve carries it.</summary>
        private static async Task<(string ItemId, string LineId, string PlanItemId)> CircularKeptAsync(DecisionFixture f)
        {
            await f.Engine.AddPlanItemAsync("measure:Sales/Old Total", "set_dax", "[Old Total] + 1",
                null, null, null, "agent");
            var row = (await f.Engine.GetModelPlanAsync()).Items
                .First(i => i.ChangeSet != null && i.ChangeSet.Lines.Any(l => string.Equals(l.Op, "set_dax", StringComparison.OrdinalIgnoreCase)));
            var line = row.ChangeSet.Lines.Single(l => string.Equals(l.Op, "set_dax", StringComparison.OrdinalIgnoreCase));
            await f.Engine.SetPlanItemAsync(line.PlanItemId, null, approved: true, "human");
            var kept = Row(await f.Engine.GetModelPlanAsync(), row.Id).ChangeSet.Lines.Single(l => l.Id == line.Id);
            Assert.True(kept.Kept, "the fixture has to keep the line, or the refusal proves nothing");
            return (row.Id, line.Id, line.PlanItemId);
        }

        [Fact]
        public async Task A_single_approve_refused_before_the_write_leaves_its_lines_pending_and_applyable()
        {
            using var f = await GappyAsync("PreflightSingle.bim");
            var (itemId, _, planItemId) = await CircularKeptAsync(f);
            var ready = Row(await f.Engine.GetModelPlanAsync(), itemId).ChangeSet;
            var linesBefore = (await f.Engine.GetPlanAsync()).Items.ToDictionary(i => i.Id, i => (i.Status, i.Note, i.VerifyState));

            var result = await f.Engine.ModelPlanActionAsync(itemId, "approve", "human", ready.Digest);

            Assert.False(result.Performed, result.Sentence);
            Assert.Contains("nothing was changed", result.Sentence, StringComparison.OrdinalIgnoreCase);
            // The refusal still says WHY, although the line it names has gone back to what it said.
            Assert.Contains("references", result.Sentence, StringComparison.OrdinalIgnoreCase);
            // EVERY LINE READS EXACTLY AS IT DID: status, note and verify state. (The ITEM is marked rolled back,
            // which is the plan's own memory of the refusal and not a line's state.)
            var linesAfter = (await f.Engine.GetPlanAsync()).Items.ToDictionary(i => i.Id, i => (i.Status, i.Note, i.VerifyState));
            Assert.Equal(linesBefore, linesAfter);
            Assert.Equal("approved", linesAfter[planItemId].Status);

            // And the line is still applyable: rewrite the value to one that does not loop, and it goes in.
            await f.Engine.SetPlanItemAsync(planItemId, "SUM ( Sales[Amount] ) + 1", approved: true, "human");
            var report = await f.Engine.ApplyPlanAsync(new[] { planItemId }, "human");
            Assert.Equal("applied", report.Items.Single(i => i.Id == planItemId).Status);
        }

        [Fact]
        public async Task A_bulk_apply_refused_before_the_write_leaves_every_line_pending_and_applyable()
        {
            using var f = await GappyAsync("PreflightBulk.bim");
            var first = OtherSafeFixRow(await f.Engine.GetModelPlanAsync()).Id;
            await f.Engine.ModelPlanActionAsync(first, "propose", "human");
            var (itemId, _, planItemId) = await CircularKeptAsync(f);
            var ready = await f.Engine.GetModelPlanAsync();
            var firstSet = Row(ready, first).ChangeSet;
            var circularSet = Row(ready, itemId).ChangeSet;
            var linesBefore = (await f.Engine.GetPlanAsync()).Items.ToDictionary(i => i.Id, i => (i.Status, i.Note, i.VerifyState));
            var sent = new[]
            {
                new PlanItemDigest { Id = first, Digest = firstSet.Digest },
                new PlanItemDigest { Id = itemId, Digest = circularSet.Digest },
            };

            var result = await f.Engine.ApplyPlanItemsAsync(sent, "human");

            Assert.False(result.Performed, result.Sentence);
            Assert.Equal(0, result.Applied);
            Assert.Equal(ready.Revision, result.Revision);
            // EVERY line on the change plan reads as it did before the call: status, note and verify state.
            var linesAfter = (await f.Engine.GetPlanAsync()).Items.ToDictionary(i => i.Id, i => (i.Status, i.Note, i.VerifyState));
            Assert.Equal(linesBefore, linesAfter);
            var back = await f.Engine.GetModelPlanAsync();
            Assert.Equal(firstSet.Digest, Row(back, first).ChangeSet.Digest);
            Assert.Equal(ModelPlanItemStates.Proposed, Row(back, first).State);

            // Every line still applies: the first item on its own, the looping line once its value is rewritten.
            var alone = await f.Engine.ApplyPlanItemsAsync(new[] { sent[0] }, "human");
            Assert.True(alone.Performed, alone.Sentence);
            Assert.Equal(firstSet.KeptCount, alone.Applied);
            await f.Engine.SetPlanItemAsync(planItemId, "SUM ( Sales[Amount] ) + 1", approved: true, "human");
            var report = await f.Engine.ApplyPlanAsync(new[] { planItemId }, "human");
            Assert.Equal("applied", report.Items.Single(i => i.Id == planItemId).Status);
        }

        private static string Lower(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);
    }
}
