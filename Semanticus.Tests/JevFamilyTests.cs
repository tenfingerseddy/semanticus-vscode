// The first Jev family on the Plan: measures (T268, jev-groundwork-contract.md section 2), and the four new
// plan executor kinds the contract adds for the families that follow.
//
// WHAT IS PROVED HERE, AND HOW.
//   Free composes nothing and asks nothing. Counted off the recorded oracle, never argued from the code.
//   Pro composes one item per table, in Meaning, owned by Jev, already proposed, with every line mapped to
//   an executor kind, carrying the point, the confidence word, an engine-built reason and the engine's check.
//   Unsure arrives dropped, and approving the item writes only what was kept.
//   Each new executor kind validates: a data type change that would lose data is refused with a plain
//   sentence, and a sort-by that is not one to one is refused.
//
// THE DIGESTS ARE DISCOVERED, NOT WRITTEN DOWN. The recorded oracle is keyed on the whole request, and the
// family asks exactly the request propose_measure_defaults asks, so DecisionFixture.DiscoverDigestAsync reads
// each measure's real digest off the engine. That also proves the family reuses the point's request rather
// than building a second one: if it did not, no fixture built this way would ever answer it.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class JevFamilyTests : IClassFixture<ModelTemplates>
    {
        private const string Point = "measure.folder_and_format";

        // Lean suite (8 Oct): each model is built once per class (ModelTemplates) and every test opens its own copy.
        private readonly ModelTemplates _templates;
        public JevFamilyTests(ModelTemplates templates) => _templates = templates;

        /// <summary>Two tables, three measures that want a folder and a format, and two that already have
        /// both. "Money" carries a money format, so the currency family has a format this model already uses.</summary>
        private async Task<DecisionFixture> MeasuresModelAsync(string file, bool pro = true)
        {
            var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            await _templates.OpenCopyAsync(f, "measures", BuildMeasuresAsync, file);
            f.UseNull();
            return f;
        }

        private static async Task BuildMeasuresAsync(DecisionFixture f)
        {
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            var revenue = await f.Engine.CreateMeasureAsync("table:Sales", "Revenue", "SUM ( Sales[Amount] )", "human", "Money");
            await f.Engine.SetMeasureFormatAsync(revenue, "$#,##0.00", "human");
            var orders = await f.Engine.CreateMeasureAsync("table:Sales", "Order Count", "COUNTROWS ( Sales )", "human", "Volume");
            await f.Engine.SetMeasureFormatAsync(orders, "#,##0", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Margin Pct", "DIVIDE ( [Revenue], [Total Sales] )", "human");
            await f.Engine.CreateTableAsync("Returns", "human");
            await f.Engine.CreateColumnAsync("table:Returns", "Qty", "Int64", null, "human");
            await f.Engine.CreateMeasureAsync("table:Returns", "Return Count", "COUNTROWS ( Returns )", "human");
        }

        private sealed class Answer
        {
            public string Folder; public double FolderConfidence;
            public string Format; public double FormatConfidence;
        }

        /// <summary>One recorded fixture answering several measures, each at its own real digest.</summary>
        private static string Fixture(IEnumerable<(string Digest, Answer A)> rows)
        {
            string N(double d) => d.ToString(CultureInfo.InvariantCulture);
            var sb = new StringBuilder("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[");
            var first = true;
            foreach (var (digest, a) in rows)
            {
                if (!first) sb.Append(',');
                first = false;
                var folderId = a.Folder == null ? DecisionRegistry.NoFolder : MeasureDefaultsPoint.FolderOptionId(a.Folder);
                sb.Append("{\"digest\":").Append(JsonSerializer.Serialize(digest))
                  .Append(",\"point\":\"").Append(Point).Append('"')
                  .Append(",\"usage\":{\"input_tokens\":900,\"output_tokens\":40}")
                  .Append(",\"answers\":{")
                  .Append("\"display_folder\":{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(folderId))
                  .Append(",\"confidence\":").Append(N(a.FolderConfidence)).Append('}')
                  .Append(",\"format_family\":{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(a.Format))
                  .Append(",\"confidence\":").Append(N(a.FormatConfidence)).Append('}')
                  .Append("}}");
            }
            return sb.Append("]}").ToString();
        }

        /// <summary>The standard answers: Total Sales is money and sure, Margin Pct is a share Jev is unsure
        /// of (its folder is only likely), and Return Count is a count, sure.</summary>
        private static async Task<ByObjectRecordedOracle> AnswerAllAsync(DecisionFixture f)
        {
            await f.ConsentAsync();
            // The family asks a table's measures in one request (m11/jev-batch), so an entry is keyed by the measure's ref, not by a digest.
            var total = "measure:Sales/Total Sales";
            var margin = "measure:Sales/Margin Pct";
            var returns = "measure:Returns/Return Count";
            return f.UseRecordedByObject(Fixture(new[]
            {
                (total, new Answer { Folder = "Money", FolderConfidence = 0.93, Format = "currency", FormatConfidence = 0.95 }),
                (margin, new Answer { Folder = "Money", FolderConfidence = 0.72, Format = "percent", FormatConfidence = 0.41 }),
                (returns, new Answer { Folder = "Volume", FolderConfidence = 0.9, Format = "whole_number", FormatConfidence = 0.92 }),
            }));
        }

        /// <summary>Read the plan, let the background run land, and read again. The plan never waits for Jev,
        /// so a test that wants the Jev items reads twice, exactly as the page does after plan/didChange.</summary>
        private static async Task<ModelPlanView> SettledPlanAsync(DecisionFixture f)
        {
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            return await f.Engine.GetModelPlanAsync();
        }

        /// <summary>Read the plan once the value ranking for what it shows has landed too. The Jev items arriving
        /// change what is ranked, so the read after they land starts a ranking run of its own, and jevPending
        /// stays set until that run lands (the page re-reads on plan/didChange twice, exactly like this).</summary>
        private static async Task<ModelPlanView> RankedPlanAsync(DecisionFixture f)
        {
            await f.Engine.GetModelPlanAsync();
            await f.Engine.PlanRankSettledForTest();
            return await f.Engine.GetModelPlanAsync();
        }

        /// <summary>A slow oracle: every measures-family ask waits until the test lets it go, then answers from
        /// the recorded fixture. Counts the asks that were cancelled while they waited. Other points (the plan's
        /// value ranking) pass straight through: this proves the FAMILY no longer holds the plan, and the
        /// ranking is a separate ask with its own finding.</summary>
        private sealed class GatedOracle : IDecisionOracle
        {
            private readonly IDecisionOracle _inner;
            private readonly TaskCompletionSource<bool> _release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _waiting, _cancelled;
            public GatedOracle(IDecisionOracle inner) { _inner = inner; }
            public bool Available => true;
            public int Waiting => Volatile.Read(ref _waiting);
            public int Cancelled => Volatile.Read(ref _cancelled);
            public void Release() => _release.TrySetResult(true);
            public async Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                if (!string.Equals(request.PointName, Point, StringComparison.Ordinal)) return await _inner.AskAsync(request, ct);
                Interlocked.Increment(ref _waiting);
                try { await _release.Task.WaitAsync(ct); }
                catch (OperationCanceledException) { Interlocked.Increment(ref _cancelled); throw; }
                finally { Interlocked.Decrement(ref _waiting); }
                return await _inner.AskAsync(request, ct);
            }
        }

        private static GatedOracle UseGated(DecisionFixture f, IDecisionOracle recorded)
        {
            var gated = new GatedOracle(recorded);
            f.Engine.UseDecisionsForTest(new DecisionService(gated, new DecisionBudget()));
            return gated;
        }

        private static async Task<T> Within<T>(Task<T> task, string what)
        {
            var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.True(ReferenceEquals(done, task), what);
            return await task;
        }

        // ---- free -------------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_free_session_composes_no_Jev_item_and_asks_nothing()
        {
            using var f = await MeasuresModelAsync("JevFree.bim", pro: false);
            await f.ConsentAsync();
            // An oracle that WOULD answer anything it was asked, so a zero below is the gate and not an
            // empty fixture.
            var oracle = f.UseRecorded(Fixture(new[]
            {
                ("0000000000000000000000000000000000000000000000000000000000000000",
                 new Answer { Folder = "Money", FolderConfidence = 0.99, Format = "currency", FormatConfidence = 0.99 }),
            }));

            var plan = await f.Engine.GetModelPlanAsync();

            Assert.DoesNotContain(plan.Items, i => i.Who == ModelPlanOwners.Jev);
            Assert.DoesNotContain(plan.Items, i => i.Id.StartsWith("jev.", StringComparison.Ordinal));
            Assert.Equal(0, oracle.AskCount);
            Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
            // The plan never waits for Jev, and a free session never starts the background run either, not
            // on a read and not after a model change.
            Assert.False(plan.JevPending);
            await f.Engine.SetDescriptionAsync("table:Returns", "Goods sent back.", "human");
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            Assert.Equal(0, f.Engine.JevRunsStartedForTest);
            Assert.Equal(0, oracle.AskCount);
            // Nor does it measure the model for a family that is not allowed to run.
            Assert.Equal(0, f.Engine.FactsComputationsForTest);
        }

        [Fact]
        public async Task A_Pro_session_with_no_permission_composes_no_Jev_item_and_asks_nothing()
        {
            using var f = await MeasuresModelAsync("JevNoConsent.bim");
            var oracle = f.UseEmptyRecorded();

            var plan = await f.Engine.GetModelPlanAsync();

            Assert.DoesNotContain(plan.Items, i => i.Who == ModelPlanOwners.Jev);
            Assert.Equal(0, oracle.AskCount);
        }

        // ---- Pro --------------------------------------------------------------------------------------

        /// <summary>The other half of PlanStripTests.Every_ask_failing_reads_the_rules_line_not_jev: when the family's
        /// asks come back answered, the strip does say Jev updated the plan, with no ranking in play.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_run_that_heard_back_says_Jev_updated_the_plan()
        {
            using var f = await MeasuresModelAsync("JevStrip.bim");
            await AnswerAllAsync(f);

            await SettledPlanAsync(f);
            var plan = await RankedPlanAsync(f);

            Assert.False(plan.JevPending);
            Assert.False(plan.Ranked);
            Assert.Equal(PlanStripUpdatedBy.Jev, plan.Strip.Updated.By);
        }

        [Fact]
        public async Task Pro_composes_one_proposed_Jev_item_per_table_in_Meaning()
        {
            using var f = await MeasuresModelAsync("JevPro.bim");
            await AnswerAllAsync(f);

            var plan = await SettledPlanAsync(f);
            var jev = plan.Items.Where(i => i.Who == ModelPlanOwners.Jev).OrderBy(i => i.Id, StringComparer.Ordinal).ToArray();

            Assert.Equal(new[] { "jev.measures.table:Returns", "jev.measures.table:Sales" }, jev.Select(i => i.Id).ToArray());
            var sales = jev.Single(i => i.Id == "jev.measures.table:Sales");
            Assert.Equal(ModelPlanSections.Meaning, sales.Section);
            Assert.Equal("Add folders and formats to 2 measures in Sales", sales.Title);
            Assert.Equal(ModelPlanItemStates.Proposed, sales.State);
            Assert.NotNull(sales.ChangeSet);
            Assert.Equal(ModelPlanChangeStates.Proposed, sales.ChangeSet.State);
            Assert.False(sales.ChangeSet.Stale, sales.ChangeSet.StaleReason);
            Assert.False(string.IsNullOrWhiteSpace(sales.ChangeSet.Digest));
            Assert.Equal("Add folders and formats to 1 measure in Returns", jev.Single(i => i.Id == "jev.measures.table:Returns").Title);

            // The measures that already have both are left alone.
            Assert.DoesNotContain(sales.ChangeSet.Lines, l => l.Target == "measure:Sales/Revenue" || l.Target == "measure:Sales/Order Count");

            foreach (var line in jev.SelectMany(i => i.ChangeSet.Lines))
            {
                Assert.Equal(ModelPlanChangeKinds.Decision, line.Provenance.Kind);
                Assert.Equal(Point, line.Provenance.Point);
                Assert.NotNull(line.Provenance.Confidence);
                // No confidence word on a suggestion, on either door (addendum 3 item 17): "sure" beside an unproved format says the opposite of "check it fits".
                if (line.Suggestion) Assert.Null(line.Provenance.ConfidenceWord);
                else Assert.Contains(line.Provenance.ConfidenceWord, new[] { "sure", "likely", "unsure" });
                Assert.False(string.IsNullOrWhiteSpace(line.Reason), line.Id);
                Assert.True(line.Suggestion || line.Check != null, line.Id);
                // A suggestion is not a check: its plain note is its own field and it carries no check.
                var checkText = line.Suggestion ? line.Note : line.Check.Text;
                Assert.True(line.Suggestion ? line.Check == null : line.Check.Exists, line.Id);
                Assert.False(string.IsNullOrWhiteSpace(checkText), line.Id);
                Assert.DoesNotContain("\u2014", line.Reason, StringComparison.Ordinal);
                Assert.DoesNotContain("\u2014", checkText, StringComparison.Ordinal);
            }

            var money = sales.ChangeSet.Lines.Single(l => l.Target == "measure:Sales/Total Sales" && l.Op == "set_measure_format");
            Assert.Equal("$#,##0.00", money.After);   // the money format this model already uses, never a guessed currency
            Assert.Null(money.Provenance.ConfidenceWord);   // a suggestion carries no word (addendum 3 item 17)
            Assert.True(money.Provenance.Confidence >= 0.9, "the number is still sent");
            // Total Sales is a SUM of a Double column: the engine cannot prove it is not a rate or an amount with cents, so the
            // format is only a suggestion (the format fill rule): not kept, and it says so in plain words.
            Assert.False(money.Kept);
            Assert.Equal("Jev suggests this format; check it fits before you run it.", money.Note);
            Assert.True(money.Suggestion);
            Assert.Null(money.Check);
            var folder = sales.ChangeSet.Lines.Single(l => l.Target == "measure:Sales/Total Sales" && l.Op == "set_display_folder");
            Assert.Equal("Money", folder.After);
            Assert.Equal("likely", sales.ChangeSet.Lines.Single(l => l.Target == "measure:Sales/Margin Pct" && l.Op == "set_display_folder").Provenance.ConfidenceWord);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Every_Jev_line_maps_to_an_executor_kind_and_approving_it_writes_the_kept_lines()
        {
            using var f = await MeasuresModelAsync("JevApprove.bim");
            await AnswerAllAsync(f);

            var plan = await SettledPlanAsync(f);
            var returns = plan.Items.Single(i => i.Id == "jev.measures.table:Returns");
            Assert.All(returns.ChangeSet.Lines, l => Assert.Contains(l.Op, new[] { "set_display_folder", "set_measure_format" }));

            var result = await f.Engine.ModelPlanActionAsync(returns.Id, "approve", "human", returns.ChangeSet.Digest);

            Assert.True(result.Performed, result.Sentence);
            var measure = await f.Engine.GetObjectAsync("measure:Returns/Return Count");
            Assert.Equal("Volume", measure.Properties["displayFolder"]?.ToString());
            Assert.Equal("#,##0", measure.Properties["formatString"]?.ToString());
            // The item is gone from the open plan because its gap is gone.
            Assert.DoesNotContain(result.Plan.Items, i => i.Id == returns.Id && i.State == ModelPlanItemStates.Proposed);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_unsure_line_arrives_dropped_and_approve_leaves_it_out()
        {
            using var f = await MeasuresModelAsync("JevUnsure.bim");
            await AnswerAllAsync(f);

            var plan = await SettledPlanAsync(f);
            var sales = plan.Items.Single(i => i.Id == "jev.measures.table:Sales");
            var unsure = sales.ChangeSet.Lines.Single(l => l.Target == "measure:Sales/Margin Pct" && l.Op == "set_measure_format");
            Assert.True(unsure.Suggestion, "Margin Pct is a ratio, so the line is a suggestion as well as unsure");
            Assert.Null(unsure.Provenance.ConfidenceWord);   // addendum 3 item 17: no word on a suggestion; the number stays
            Assert.True(unsure.Provenance.Confidence < 0.6, "the confidence is still sent, and it is unsure");
            Assert.False(unsure.Kept);
            // R2 (m11/jev-batch): a folder at "likely" (0.72, under the 0.85 bar) arrives UNTICKED, as the registry promises
            // ("the proposal is left out"). A sure folder (Total Sales, 0.93) is still approved.
            var likelyFolder = sales.ChangeSet.Lines.Single(l => l.Target == "measure:Sales/Margin Pct" && l.Op == "set_display_folder");
            Assert.Equal("likely", likelyFolder.Provenance.ConfidenceWord);
            Assert.False(likelyFolder.Kept, "a folder Jev is only likely about must arrive unticked");
            Assert.True(sales.ChangeSet.Lines.Single(l => l.Target == "measure:Sales/Total Sales" && l.Op == "set_display_folder").Kept);
            // Three lines are not kept: the unsure format (dropped), Total Sales' money format (a suggestion, never a fill) and the likely folder.
            Assert.Equal(sales.ChangeSet.Lines.Length - 3, sales.ChangeSet.KeptCount);
            Assert.Equal(new[] { "measure:Sales/Margin Pct", "measure:Sales/Total Sales" },
                sales.ChangeSet.Lines.Where(l => !l.Kept && l.Op == "set_measure_format").Select(l => l.Target).OrderBy(x => x, StringComparer.Ordinal));

            var result = await f.Engine.ModelPlanActionAsync(sales.Id, "approve", "human", sales.ChangeSet.Digest);

            Assert.True(result.Performed, result.Sentence);
            var margin = await f.Engine.GetObjectAsync("measure:Sales/Margin Pct");
            Assert.True(string.IsNullOrEmpty(margin.Properties["displayFolder"]?.ToString()), "the likely folder was not ticked, so approve leaves it");
            Assert.True(string.IsNullOrEmpty(margin.Properties["formatString"]?.ToString()));
        }

        [Fact]
        public async Task A_second_read_changes_nothing_so_a_digest_stays_approvable()
        {
            using var f = await MeasuresModelAsync("JevStable.bim");
            await AnswerAllAsync(f);

            var first = (await SettledPlanAsync(f)).Items.Single(i => i.Id == "jev.measures.table:Sales");
            var second = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == "jev.measures.table:Sales");

            Assert.Equal(first.ChangeSet.Digest, second.ChangeSet.Digest);
            Assert.Equal(first.ChangeSet.Lines.Select(l => l.PlanItemId), second.ChangeSet.Lines.Select(l => l.PlanItemId));
        }

        [Fact]
        public async Task Withdrawing_permission_takes_the_Jev_items_off_the_plan()
        {
            using var f = await MeasuresModelAsync("JevWithdraw.bim");
            await AnswerAllAsync(f);
            Assert.Contains((await SettledPlanAsync(f)).Items, i => i.Who == ModelPlanOwners.Jev);

            await f.Engine.WithdrawConsentAsync("human");

            Assert.DoesNotContain((await f.Engine.GetModelPlanAsync()).Items, i => i.Who == ModelPlanOwners.Jev);
        }

        // ---- the plan never waits for Jev ---------------------------------------------------------------

        [Fact]
        public async Task The_plan_returns_before_Jev_answers_and_the_items_arrive_on_the_next_read()
        {
            using var f = await MeasuresModelAsync("JevBackground.bim");
            var gated = UseGated(f, await AnswerAllAsync(f));
            var announced = 0;
            f.Sessions.Bus.PlanChanged += _ => Interlocked.Increment(ref announced);

            // The oracle has not answered and will not until it is let go, so a plan that came back at all
            // came back without waiting for Jev.
            var first = await Within(f.Engine.GetModelPlanAsync(), "get_model_plan waited for Jev");
            Assert.True(first.JevPending);
            Assert.DoesNotContain(first.Items, i => i.Who == ModelPlanOwners.Jev);
            Assert.Equal(1, f.Engine.JevRunsStartedForTest);

            // A second read while the run is still out starts nothing new and still says Jev is looking.
            var second = await Within(f.Engine.GetModelPlanAsync(), "a second read waited for Jev");
            Assert.True(second.JevPending);
            Assert.Equal(1, f.Engine.JevRunsStartedForTest);

            gated.Release();
            await f.Engine.JevSettledForTest();

            Assert.True(Volatile.Read(ref announced) > 0, "the page was never told the run landed");
            var after = await RankedPlanAsync(f);
            Assert.False(after.JevPending);
            Assert.Contains(after.Items, i => i.Id == "jev.measures.table:Sales" && i.Who == ModelPlanOwners.Jev);
            Assert.Contains(after.Items, i => i.Id == "jev.measures.table:Returns");
            // Reading again asks nothing more: the decision for this revision is reused.
            Assert.Equal(1, f.Engine.JevRunsStartedForTest);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_model_change_cancels_the_stale_run_and_plans_the_new_revision()
        {
            using var f = await MeasuresModelAsync("JevRestart.bim");
            var gated = UseGated(f, await AnswerAllAsync(f));

            var first = await Within(f.Engine.GetModelPlanAsync(), "get_model_plan waited for Jev");
            Assert.True(first.JevPending);
            await WaitUntil(() => gated.Waiting > 0, "the first run never asked");

            // A change the measures' requests do not read, so the recorded answers still fit the new revision.
            await f.Engine.SetDescriptionAsync("table:Returns", "Goods sent back.", "human");
            var revision = (await f.Engine.SessionInfoAsync()).Revision;

            await WaitUntil(() => gated.Cancelled > 0, "the stale run was not cancelled");
            Assert.Equal(2, f.Engine.JevRunsStartedForTest);

            gated.Release();
            await f.Engine.JevSettledForTest();
            var plan = await RankedPlanAsync(f);

            Assert.False(plan.JevPending);
            var sales = plan.Items.Single(i => i.Id == "jev.measures.table:Sales");
            Assert.False(sales.ChangeSet.Stale, sales.ChangeSet.StaleReason);
            Assert.Equal(revision, plan.Revision);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_Pro_run_builds_the_model_facts_for_its_revision_before_it_asks()
        {
            using var f = await MeasuresModelAsync("JevFactsFirst.bim");
            await AnswerAllAsync(f);
            Assert.Null(f.Engine.TryGetCurrentFacts());
            var before = f.Engine.FactsComputationsForTest;

            await SettledPlanAsync(f);

            // The run measured the model once for this revision, so the executor's sort-by and data type checks
            // have facts to read when a person applies a Jev line.
            var facts = f.Engine.TryGetCurrentFacts();
            Assert.NotNull(facts);
            Assert.Equal((await f.Engine.SessionInfoAsync()).Revision, facts.Revision);
            Assert.Equal(before + 1, f.Engine.FactsComputationsForTest);

            // A second read of the same revision measures nothing more.
            await SettledPlanAsync(f);
            Assert.Equal(before + 1, f.Engine.FactsComputationsForTest);
        }

        private static async Task WaitUntil(Func<bool> condition, string what)
        {
            for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(25);
            Assert.True(condition(), what);
        }

        // ---- one line per change ----------------------------------------------------------------------

        private static ModelPlanChangeLine[] FormatLinesFor(ModelPlanView plan, string measureRef) =>
            plan.Items.Where(i => i.ChangeSet?.Lines != null)
                .SelectMany(i => i.ChangeSet.Lines)
                .Where(l => l.Target == measureRef && string.Equals(l.Property, "formatString", StringComparison.OrdinalIgnoreCase))
                .ToArray();

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_Jev_format_suggestion_does_not_take_the_slot_from_the_format_rule_line_while_live()
        {
            using var f = await MeasuresModelAsync("JevOneLineLive.bim");
            await AnswerAllAsync(f);
            // The seed puts a rule's line on this measure's format. On this model it is BPA's INTEGER_FORMATTING
            // ("#,0", even for a share), which wins the seed's one-line-per-property slot over FMT-MEASURE; the
            // FMT-MEASURE shape itself is pinned by the unit test below. Either way it is a second proposal
            // for the property the Jev line writes.
            var seeded = await f.Engine.ProposePlanAsync(null, true, 40, "human");
            Assert.True(seeded.Items.Any(l => !string.IsNullOrEmpty(l.RuleId) && l.ObjectRef == "measure:Sales/Total Sales"
                && string.Equals(l.Target, "formatString", StringComparison.OrdinalIgnoreCase)),
                string.Join(" | ", seeded.Items.Select(l => l.RuleId + " " + l.ObjectRef + "#" + l.Target)));

            var plan = await SettledPlanAsync(f);

            var lines = FormatLinesFor(plan, "measure:Sales/Total Sales");
            // A Jev line the rule could not prove is only a SUGGESTION (addendum 2 item 8 and 11): it does not take the slot, because
            // the rule's line is what puts the choice to the person's assistant, and hiding it hid the measure from the assistant's
            // words. So both stay: the suggestion (for the person to tick) and the rule's line (for the assistant's words).
            Assert.Equal(2, lines.Length);
            var jev = Assert.Single(lines, l => l.Provenance?.Kind == ModelPlanChangeKinds.Decision);
            Assert.True(jev.Suggestion);
            Assert.Equal("$#,##0.00", jev.After);
            Assert.Single(lines, l => l.Provenance?.Kind != ModelPlanChangeKinds.Decision);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_format_rule_line_is_the_one_line_when_the_point_is_not_live()
        {
            using var f = await MeasuresModelAsync("JevOneLineOff.bim");
            await AnswerAllAsync(f);
            await f.Engine.ProposePlanAsync(null, true, 40, "human");
            // While Jev is live its suggestion and the rule's line are both there (a suggestion does not take the slot).
            Assert.Equal(2, FormatLinesFor(await SettledPlanAsync(f), "measure:Sales/Total Sales").Length);

            await f.Engine.SetDecisionPointEnabledAsync(Point, false, "human");
            var plan = await f.Engine.GetModelPlanAsync();

            var only = Assert.Single(FormatLinesFor(plan, "measure:Sales/Total Sales"));
            Assert.NotEqual(ModelPlanChangeKinds.Decision, only.Provenance?.Kind);
            Assert.DoesNotContain(plan.Items, i => i.Who == ModelPlanOwners.Jev);
        }

        [Fact]
        public void One_line_per_change_keeps_Jev_when_live_and_the_rule_otherwise()
        {
            ChangeItem Rule() => new ChangeItem
            {
                ObjectRef = "measure:Sales/Total Sales", Kind = "set_measure_format", Target = "formatString",
                Source = "ai", RuleId = "FMT-MEASURE", Status = "needs_content",
            };
            ChangeItem Jev() => new ChangeItem
            {
                ObjectRef = "measure:Sales/Total Sales", Kind = "set_measure_format", Target = "formatString",
                Source = JevFamily.Source, JevItem = "jev.measures.table:Sales", After = "$#,##0.00", Status = "approved",
            };
            ChangeItem Folder() => new ChangeItem
            {
                ObjectRef = "measure:Sales/Total Sales", Kind = "set_display_folder", Target = "displayFolder",
                Source = JevFamily.Source, JevItem = "jev.measures.table:Sales", After = "Money", Status = "approved",
            };

            var live = LocalEngine.OneLinePerChange(new[] { Rule(), Jev(), Folder() }, jevLive: true);
            Assert.Equal(2, live.Length);
            Assert.All(live, l => Assert.True(JevFamily.IsJevLine(l)));

            var off = LocalEngine.OneLinePerChange(new[] { Rule(), Jev(), Folder() }, jevLive: false);
            Assert.Equal(2, off.Length);
            Assert.Single(off, l => l.RuleId == "FMT-MEASURE");
            Assert.Single(off, l => l.Target == "displayFolder");

            // A line that already ran is a receipt and is never hidden.
            var applied = Rule(); applied.Status = "applied";
            Assert.Equal(2, LocalEngine.OneLinePerChange(new[] { applied, Jev() }, jevLive: true).Length);
        }

        // ---- the new executor kinds -------------------------------------------------------------------

        private sealed class StubFacts : IJevModelFacts
        {
            public Dictionary<string, string[]> OneToOne = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, bool> Whole = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            public IReadOnlyList<string> OneToOneWith(string columnRef) =>
                OneToOne.TryGetValue(columnRef, out var v) ? v : null;
            public bool? WholeNumbers(string columnRef) => Whole.TryGetValue(columnRef, out var v) ? v : (bool?)null;
        }

        private async Task<DecisionFixture> ColumnsModelAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "columns", BuildColumnsAsync, file);
            f.UseNull();
            return f;
        }

        private static async Task BuildColumnsAsync(DecisionFixture f)
        {
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Date", "human");
            await f.Engine.CreateColumnAsync("table:Date", "Month", "String", null, "human");
            await f.Engine.CreateColumnAsync("table:Date", "Month Number", "Int64", null, "human");
            await f.Engine.CreateColumnAsync("table:Date", "Day", "String", null, "human");
            await f.Engine.CreateColumnAsync("table:Date", "Price", "Double", null, "human");
            await f.Engine.CreateColumnAsync("table:Date", "Year", "Int64", null, "human");
        }

        private static async Task<ChangeItem> ApplyOneAsync(DecisionFixture f, string objRef, string kind, string after)
        {
            var view = await f.Engine.AddPlanItemAsync(objRef, kind, after, null, null, null, "human");
            var line = view.Items.Last(i => i.ObjectRef == objRef && i.Kind == kind);
            if (line.Status != "approved") await f.Engine.SetPlanItemAsync(line.Id, null, true, "human");
            var report = await f.Engine.ApplyPlanAsync(new[] { line.Id }, "human");
            return report.Items.Single(i => i.Id == line.Id);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_data_type_change_that_would_lose_data_is_refused_with_a_plain_sentence()
        {
            using var f = await ColumnsModelAsync("JevDataTypeLoss.bim");

            var item = await ApplyOneAsync(f, "column:Date/Price", "set_data_type", "Int64");

            Assert.Equal("failed", item.Status);
            Assert.Contains("Price", item.Note, StringComparison.Ordinal);
            Assert.Contains("lose", item.Note, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\u2014", item.Note, StringComparison.Ordinal);
            var col = await f.Engine.GetObjectAsync("column:Date/Price");
            Assert.Equal("Double", col.Properties["dataType"]?.ToString());
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_data_type_change_that_keeps_every_value_applies()
        {
            using var f = await ColumnsModelAsync("JevDataTypeWiden.bim");

            // Text holds every whole number exactly, so this needs no facts at all.
            var item = await ApplyOneAsync(f, "column:Date/Year", "set_data_type", "String");

            Assert.Equal("applied", item.Status);
            var col = await f.Engine.GetObjectAsync("column:Date/Year");
            Assert.Equal("String", col.Properties["dataType"]?.ToString());
        }

        [Fact]
        public async Task A_whole_number_to_fixed_decimal_change_nobody_measured_is_refused_with_a_plain_sentence()
        {
            using var f = await ColumnsModelAsync("JevDataTypeInt64Decimal.bim");

            // A fixed decimal holds whole numbers only up to 922,337,203,685,477 either way, and a whole number
            // column reaches 2^63, so with no facts the change is not proved safe.
            var item = await ApplyOneAsync(f, "column:Date/Year", "set_data_type", "Decimal");

            Assert.Equal("failed", item.Status);
            Assert.Contains("Year", item.Note, StringComparison.Ordinal);
            Assert.Contains("measured", item.Note, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\u2014", item.Note, StringComparison.Ordinal);
            var col = await f.Engine.GetObjectAsync("column:Date/Year");
            Assert.Equal("Int64", col.Properties["dataType"]?.ToString());
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_whole_number_to_decimal_change_nobody_measured_is_refused_with_a_plain_sentence()
        {
            using var f = await ColumnsModelAsync("JevDataTypeInt64Double.bim");

            // A decimal number holds whole numbers exactly only up to 9007199254740992 either way. Past that,
            // 9007199254740993 comes back as 9007199254740992, so with no facts the change is not proved safe.
            var item = await ApplyOneAsync(f, "column:Date/Year", "set_data_type", "Double");

            Assert.Equal("failed", item.Status);
            Assert.Contains("Year", item.Note, StringComparison.Ordinal);
            Assert.Contains("measured", item.Note, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\u2014", item.Note, StringComparison.Ordinal);
            var col = await f.Engine.GetObjectAsync("column:Date/Year");
            Assert.Equal("Int64", col.Properties["dataType"]?.ToString());
        }

        [Fact]
        public async Task A_narrowing_the_facts_prove_is_whole_numbers_applies()
        {
            using var f = await ColumnsModelAsync("JevDataTypeWhole.bim");
            var facts = new StubFacts();
            facts.Whole["column:Date/Price"] = true;
            f.Engine.JevFactsForTest = () => facts;

            var item = await ApplyOneAsync(f, "column:Date/Price", "set_data_type", "Int64");

            Assert.Equal("applied", item.Status);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_sort_by_that_is_not_one_to_one_is_refused()
        {
            using var f = await ColumnsModelAsync("JevSortRefused.bim");
            var facts = new StubFacts();
            facts.OneToOne["column:Date/Month"] = new[] { "column:Date/Month Number" };
            f.Engine.JevFactsForTest = () => facts;

            var refused = await ApplyOneAsync(f, "column:Date/Day", "set_sort_by_column", "Month Number");
            Assert.Equal("failed", refused.Status);
            Assert.Contains("one to one", refused.Note, StringComparison.OrdinalIgnoreCase);

            var applied = await ApplyOneAsync(f, "column:Date/Month", "set_sort_by_column", "Month Number");
            Assert.Equal("applied", applied.Status);
            var col = await f.Engine.GetObjectAsync("column:Date/Month");
            Assert.Equal("Month Number", col.Properties["sortByColumn"]?.ToString());
        }

        [Fact]
        public async Task A_sort_by_nobody_measured_is_refused_rather_than_guessed()
        {
            using var f = await ColumnsModelAsync("JevSortUnmeasured.bim");

            var item = await ApplyOneAsync(f, "column:Date/Month", "set_sort_by_column", "Month Number");

            Assert.Equal("failed", item.Status);
            Assert.Contains("measured", item.Note, StringComparison.OrdinalIgnoreCase);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Is_available_in_MDX_is_written_and_refused_for_a_column_something_sorts_by()
        {
            using var f = await ColumnsModelAsync("JevMdx.bim");
            var facts = new StubFacts();
            facts.OneToOne["column:Date/Month"] = new[] { "column:Date/Month Number" };
            f.Engine.JevFactsForTest = () => facts;

            var off = await ApplyOneAsync(f, "column:Date/Day", "set_is_available_in_mdx", "false");
            Assert.Equal("applied", off.Status);

            Assert.Equal("applied", (await ApplyOneAsync(f, "column:Date/Month", "set_sort_by_column", "Month Number")).Status);
            var refused = await ApplyOneAsync(f, "column:Date/Month Number", "set_is_available_in_mdx", "false");
            Assert.Equal("failed", refused.Status);
            Assert.Contains("sort", refused.Note, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Set_property_writes_a_census_enum_and_refuses_anything_else()
        {
            using var f = await ColumnsModelAsync("JevSetProperty.bim");
            var context = f.Sessions.CurrentContext;
            var s = context.RequireSession();

            ChangeItem Seed(string target, string after, string column = "Price") => new ChangeItem
            {
                ObjectRef = "column:Date/" + column, ObjectName = column, Kind = "set_property", Source = "deterministic",
                Target = target, Before = target == "Alignment" ? "Default" : null, After = after, Status = "approved", Title = "Set " + target,
            };
            var good = Seed("Alignment", "Right");
            var badValue = Seed("Alignment", "Sideways", "Year");
            var notCensus = Seed("Name", "Cost");
            await context.PlanGate.WaitAsync();
            try
            {
                var plan = context.Plans.GetOrStart();
                plan.Add(good, s.Revision, "");
                plan.Add(badValue, s.Revision, "");
                plan.Add(notCensus, s.Revision, "");
            }
            finally { context.PlanGate.Release(); }

            var report = await f.Engine.ApplyPlanAsync(new[] { good.Id, badValue.Id, notCensus.Id }, "human");

            Assert.Equal("applied", report.Items.Single(i => i.Id == good.Id).Status);
            var wrongValue = report.Items.Single(i => i.Id == badValue.Id);
            Assert.Equal("failed", wrongValue.Status);
            Assert.Contains("Sideways", wrongValue.Note, StringComparison.Ordinal);
            var wrongProperty = report.Items.Single(i => i.Id == notCensus.Id);
            Assert.Equal("failed", wrongProperty.Status);
            Assert.Contains("Name", wrongProperty.Note, StringComparison.Ordinal);
            var col = await f.Engine.GetObjectAsync("column:Date/Price");
            Assert.Equal("Price", col.Name);
        }
    }
}
