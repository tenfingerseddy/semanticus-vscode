// Fix round 2 of lane m11/sf-decide (the verifier's two harms and the join items it named). Each test here failed on 560c2f00 before the fix:
//   consent (no ask before the start plan's own grant, by any path), the assistant's words held and not deleted, a pick Jev keeps flipping,
//   a plan unparked by its model opening again, the sample facts the scratch carries, and a suggestion left unticked that the Plan must not offer again.
// The rig is StartPlanDecideTests': a scratch built on a second engine from the manifest's spec, the kept lines applied by the Plan's executor.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.Facts;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class StartPlanDecideRound2Tests
    {
        private static string Describe(StartPlanDoc doc) => doc.Decide.State + " round " + doc.Decide.Round + ": " + doc.Decide.Reason;

        private static async Task<StartPlanDecideTests.Rig> RigAsync(IDecisionOracle oracle) =>
            await StartPlanDecideTests.Rig.CreateAsync(SqlDraftBuildTests.J8Schema(), oracle);

        private static StartLine ColumnWords(string table, string column, string text) => new StartLine
        {
            Item = new ChangeItem
            {
                ObjectRef = "column:" + table + "/" + column, ObjectName = column, Kind = "set_description", Target = "description",
                After = text, Source = "assistant", Status = "approved", Title = "Describe " + column,
            },
            DecidedBy = StartDecidedBy.Assistant, Word = text, Kept = true, Round = 0,
        };

        // ================================================================== 1. consent: the plan's own grant, on every ask

        [Fact]
        public async Task A_plan_with_no_disclosure_sends_nothing_even_when_the_model_may_use_Jev_and_the_plan_says_deciding()
        {
            var oracle = new VerifyTextOracle();
            using var rig = await RigAsync(oracle);
            rig.Doc.Disclosure = null;
            rig.Doc.Decide.State = StartDecideState.Deciding;
            Assert.True(await rig.DecideAsync());
            Assert.Equal(0, oracle.Asks);
            Assert.Equal(StartDecideState.WaitingGrant, rig.Doc.Decide.State);
            Assert.Equal(0, rig.Host.Builds);   // not even a scratch was built for it
        }

        [Fact]
        public async Task A_table_added_after_the_grant_stops_every_ask_not_only_the_asks_about_it()
        {
            var oracle = new VerifyTextOracle();
            using var rig = await RigAsync(oracle);
            var staff = rig.Doc.Manifest.Tables.Single(t => t.Name == "dim_staff");
            rig.Doc.Disclosure.IncludedTables = rig.Doc.Disclosure.IncludedTables.Where(n => !string.Equals(n, StartPlanDecide.SourceNameOf(staff), StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.True(await rig.DecideAsync());
            Assert.Equal(0, oracle.Asks);
            Assert.Equal(StartDecideState.WaitingGrant, rig.Doc.Decide.State);
            Assert.Contains("added after you agreed", rig.Doc.Decide.Reason);
        }

        [Fact]
        public async Task A_table_left_out_since_the_grant_is_narrower_and_the_asks_go_on()
        {
            var oracle = new VerifyTextOracle();
            using var rig = await RigAsync(oracle);
            Assert.True(StartPlanEditing.Apply(rig.Doc, new StartPlanEdit { Op = "include_table", Target = "dim_staff", Value = "false" }, "human").Ok);
            Assert.True(await rig.DecideAsync());
            Assert.True(oracle.Asks > 0, Describe(rig.Doc));
            Assert.Equal(StartDecideState.Stable, rig.Doc.Decide.State);
        }

        [Fact]
        public void The_sample_flag_and_the_source_must_match_what_was_granted()
        {
            var doc = StartPlanDecideTests.DocFor(SqlDraftBuildTests.J8Schema());
            doc.Decide.State = StartDecideState.Deciding;
            Assert.Null(StartPlanDecide.WhyNoAsk(doc, false));
            Assert.Contains("switched on after you agreed", StartPlanDecide.WhyNoAsk(doc, true));
            doc.Disclosure.SampleFacts = true;
            Assert.Contains("switched off after you agreed", StartPlanDecide.WhyNoAsk(doc, false));
            Assert.Null(StartPlanDecide.WhyNoAsk(doc, true));
            doc.Disclosure.SourceKey = "other-server/OtherDb";
            Assert.Contains("different source", StartPlanDecide.WhyNoAsk(doc, true));
            // The wait is not a bar of its own: a plan that waits for its grant leaves the wait the moment the grant it has covers what it discloses.
            doc.Decide.State = StartDecideState.WaitingGrant;
            Assert.Contains("different source", StartPlanDecide.WhyNoAsk(doc, true));
            doc.Disclosure.SourceKey = "contoso-sql.example.com/ContosoConsulting";
            Assert.Null(StartPlanDecide.WhyNoAsk(doc, true));
        }

        /// <summary>The fence is the per-ask check: the bar is read again when each ask is about to go, and a bar that goes up after the loop began stops
        /// every ask that has not left yet. The grant is taken back while the first ask is on its way: at most the asks already in flight (six at a time)
        /// were sent, the rest never leave, and the plan says it waits for the grant, not that "the model changed".</summary>
        [Fact]
        public async Task A_grant_taken_back_after_the_loop_began_stops_the_asks_that_have_not_left()
        {
            var withdrawn = 0;
            var oracle = new VerifyTextOracle { BeforeAnswer = (r, ct) => { Interlocked.Exchange(ref withdrawn, 1); return Task.CompletedTask; } };
            using var rig = await RigAsync(oracle);
            Func<bool, string> bar = _ => Volatile.Read(ref withdrawn) == 1 ? "The permission for this plan was taken back." : null;
            await (await rig.Engine.KickStartDecideAsync(rig.Doc, rig.Host, null, bar));
            Assert.InRange(oracle.Asks, 1, 6);
            Assert.Equal(StartDecideState.WaitingGrant, rig.Doc.Decide.State);
            Assert.Contains("taken back", rig.Doc.Decide.Reason);
        }

        // ================================================================== 2. lost work: held, not deleted

        [Fact]
        public async Task A_round_run_while_a_table_is_out_holds_the_words_and_counts_them_in_neither_number_and_they_land_again_with_the_table()
        {
            const string text = "The staff member's full name, written by the assistant.";
            var oracle = new VerifyTextOracle();
            using var rig = await RigAsync(oracle);
            Assert.True(await rig.DecideAsync());
            var withoutWords = rig.Doc.Words.Total - rig.Doc.Words.Open;
            rig.Doc.Lines.Add(ColumnWords("dim_staff", "EMP_NM", text));

            Assert.True(StartPlanEditing.Apply(rig.Doc, new StartPlanEdit { Op = "include_table", Target = "dim_staff", Value = "false" }, "human").Ok);
            Assert.True(await rig.DecideAsync());
            var held = rig.Doc.Lines.Single(l => l.Word == text);
            Assert.True(held.Kept);
            Assert.False(StartPlanDecide.IsLeftOut(held), "a held word is not 'left out because it does not apply': " + held.Why);
            Assert.Equal(withoutWords, rig.Doc.Words.Total - rig.Doc.Words.Open);   // held: in neither number
            Assert.Equal(StartDecideState.Stable, rig.Doc.Decide.State);

            Assert.True(StartPlanEditing.Apply(rig.Doc, new StartPlanEdit { Op = "include_table", Target = "dim_staff", Value = "true" }, "human").Ok);
            Assert.True(await rig.DecideAsync());
            Assert.Single(rig.Doc.Lines, l => l.Word == text);
            Assert.Equal(withoutWords + 1, rig.Doc.Words.Total - rig.Doc.Words.Open);   // back with its table, and written
        }

        [Fact]
        public void Leaving_a_table_out_says_the_words_were_held_and_removes_only_what_is_not_the_assistants_words()
        {
            const string text = "The staff member's full name, written by the assistant.";
            var doc = StartPlanDecideTests.DocFor(SqlDraftBuildTests.J8Schema());
            doc.Lines.Add(ColumnWords("dim_staff", "EMP_NM", text));
            doc.Lines.Add(new StartLine
            {
                Item = new ChangeItem { ObjectRef = "column:dim_staff/GRADE_CD", Kind = "set_description", Target = "description", After = "x", Status = "approved" },
                DecidedBy = StartDecidedBy.Jev, Kept = true,
            });
            var outcome = StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "include_table", Target = "dim_staff", Value = "false" }, "human");
            Assert.True(outcome.Ok);
            Assert.Single(doc.Lines, l => l.Word == text);
            Assert.DoesNotContain(doc.Lines, l => l.Item.ObjectRef == "column:dim_staff/GRADE_CD");
            Assert.Contains(outcome.KnockOns, k => k.Action == "held" && k.Ref == "column:dim_staff/EMP_NM" && k.Because.Contains("dim_staff"));
            Assert.Contains(outcome.KnockOns, k => k.Action == "removed" && k.Ref == "column:dim_staff/GRADE_CD");
        }

        // ================================================================== 3. a pick Jev keeps flipping

        [Fact]
        public void A_pick_that_changes_twice_in_one_decide_is_frozen_as_the_rules_pick_and_no_later_round_moves_it()
        {
            var doc = StartPlanDecideTests.DocFor(SqlDraftBuildTests.J8Schema());
            var bm = doc.Manifest.BaseMeasures.First(b => b.Pick != StarterMeasureTemplates.None);
            var other = bm.Pick == "sum" ? "average" : "sum";
            var flips = new Dictionary<string, int>(StringComparer.Ordinal);
            LocalEngine.SummarizePick Pick(string pick) => new LocalEngine.SummarizePick { TableName = bm.Table, ColumnName = bm.Column, Pick = pick, MeasureName = bm.Name, Sure = true };

            Assert.Equal(1, StartPlanDecide.FoldPicks(doc.Manifest, new[] { Pick(other) }, flips));   // first change: Jev's new sure pick
            Assert.Equal(StartDecidedBy.Jev, bm.DecidedBy);
            Assert.Equal(0, StartPlanDecide.FoldPicks(doc.Manifest, new[] { Pick(bm.Pick == "sum" ? "average" : "sum") }, flips));   // second: frozen where it is, no change to the model
            Assert.Equal(StartDecidedBy.Rule, bm.DecidedBy);
            var frozen = bm.Pick;
            Assert.Equal(0, StartPlanDecide.FoldPicks(doc.Manifest, new[] { Pick("count"), Pick(frozen) }, flips));   // and nothing moves it, not even the same pick said sure again
            Assert.Equal((frozen, StartDecidedBy.Rule), (bm.Pick, bm.DecidedBy));
            doc.Decide.State = StartDecideState.Stable;
            Assert.Contains(StartPlanDecide.NeedsYou(doc), n => n.Kind == StartNeedKind.UnsurePick && n.Id.EndsWith(bm.Identity, StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_pick_Jev_keeps_flipping_ends_the_loop_and_is_handed_to_the_person_never_called_settled_quietly()
        {
            var oracle = new VerifyTextOracle { FlipRef = "column:fact_invoice/TAX_AMT" };
            using var rig = await RigAsync(oracle);
            Assert.True(await rig.DecideAsync());
            Assert.InRange(rig.Doc.Decide.Round, 1, LocalEngine.StartDecideMaxRounds);
            var bm = rig.Doc.Manifest.BaseMeasures.FirstOrDefault(b => b.Table == "fact_invoice" && b.Column == "TAX_AMT");
            Assert.NotNull(bm);
            Assert.Equal(StartDecidedBy.Rule, bm.DecidedBy);   // never shown as Jev's sure pick
            Assert.Contains(StartPlanDecide.NeedsYou(rig.Doc), n => n.Kind == StartNeedKind.UnsurePick && n.Id.EndsWith(bm.Identity, StringComparison.Ordinal));
        }

        // ================================================================== 6. a suggestion left unticked is not offered again after the build

        [Fact]
        public async Task A_suggestion_the_person_left_unticked_is_not_offered_again_by_the_Plan_after_the_build_and_can_be_kept_from_the_Plan()
        {
            var policy = new StartPlanDecideTests.PolicyOracle();
            using var rig = await RigAsync(policy);
            Assert.True(await rig.DecideAsync());
            var unticked = rig.Doc.Lines.Where(l => !l.Kept && !StartPlanDecide.IsLeftOut(l) && JevFamily.IsJevLine(l.Item)).ToList();
            Assert.NotEmpty(unticked);   // the format suggestions Jev could not prove

            await StartPlanDecideTests.BuildLikeTheRealBuildAsync(rig);
            Assert.Equal(unticked.Count, rig.Engine.HonourStartPlanDeclines(rig.Doc));
            await rig.Engine.GetModelPlanAsync();
            await rig.Engine.JevSettledForTest();
            await rig.Engine.GetModelPlanAsync();

            string Key(string kind, string objectRef, string target, string after) => kind + "|" + objectRef + "|" + target + "|" + after;
            var theirs = new HashSet<string>(unticked.Select(l => Key(l.Item.Kind, l.Item.ObjectRef, l.Item.Target, l.Item.After)), StringComparer.OrdinalIgnoreCase);
            var items = (await rig.Engine.GetPlanAsync()).Items.Where(i => theirs.Contains(Key(i.Kind, i.ObjectRef, i.Target, i.After))).ToList();
            Assert.NotEmpty(items);
            Assert.All(items, i => Assert.True(i.Status == "rejected", "offered again: " + i.Kind + " " + i.ObjectRef + "=" + i.After + " [" + i.Status + "]"));
            Assert.All(items, i => Assert.Equal("person", i.DroppedBy));   // undone the way any drop is, by keeping the row
        }
    }

    /// <summary>The verbs over the REAL engine and the REAL scratch host: what an edit answers, the unpark, and the sample facts.</summary>
    [Collection("restore-root")]
    public sealed class StartPlanDecideRound2VerbTests : IDisposable
    {
        private readonly string _root;
        private readonly string _origHome;
        private readonly DecisionFixture _f;

        public StartPlanDecideRound2VerbTests()
        {
            _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "r2-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(_root, "home", ".semanticus"));
            _origHome = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, System.IO.Path.Combine(_root, "home", ".semanticus"));
            _f = new DecisionFixture(TestEntitlements.Pro);
            _f.Engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(SqlDraftBuildTests.J8Schema());
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, _origHome);
            _f.Dispose();
            try { System.IO.Directory.Delete(_root, true); } catch { }
        }

        private LocalEngine Engine => _f.Engine;

        private async Task StartAsync() =>
            await Engine.StartModelFromSqlAsync(new StartModelRequest
            {
                Server = "contoso-sql.example.com", Database = "ContosoConsulting", Name = "Uplift",
                Folder = System.IO.Path.Combine(_root, "dest"),
            }, "human");

        [Fact]
        public async Task The_edit_answer_lists_the_words_it_held_and_they_are_still_on_the_plan_and_come_back_with_the_table()
        {
            const string text = "The staff member's full name, written by the assistant.";
            await StartAsync();
            Engine.StartPlanDocForTestMutate(doc =>
            {
                doc.Decide = new StartDecide { State = StartDecideState.WaitingGrant };   // no loop: this is about what the edit says
                doc.Lines.Add(new StartLine
                {
                    Item = new ChangeItem { ObjectRef = "column:dim_staff/EMP_NM", ObjectName = "EMP_NM", Kind = "set_description", Target = "description", After = text, Source = "assistant", Status = "approved" },
                    DecidedBy = StartDecidedBy.Assistant, Word = text, Kept = true,
                });
            });
            var view = await Engine.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = "dim_staff", Value = "false" }, "human");
            Assert.NotNull(view.KnockOns);
            Assert.Contains(view.KnockOns, k => k.Action == "held" && k.Ref == "column:dim_staff/EMP_NM");
            Assert.Single(Engine.StartPlanDocForTest().Lines, l => l.Word == text);
            var back = await Engine.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = "dim_staff", Value = "true" }, "human");
            Assert.Null(back.KnockOns);   // nothing else moved
            Assert.Single(Engine.StartPlanDocForTest().Lines, l => l.Word == text);
        }

        [Fact]
        public async Task A_plan_whose_model_opens_again_is_decided_again_and_not_left_deciding_with_no_loop()
        {
            await StartAsync();
            await _f.ConsentAsync();
            Engine.UseDecisionsForTest(new DecisionService(new VerifyTextOracle()));
            // Parked mid-loop: the loop was cancelled and the plan reads "deciding". Its model is open (as when the person opens it again).
            Engine.StartPlanDocForTestMutate(doc => { doc.Decide = new StartDecide { State = StartDecideState.Deciding }; doc.Parked = true; });
            await Engine.GetStartPlanAsync();   // the first read after the model is open again is what unparks it
            await Engine.StartDecideSettledForTest();
            var doc = Engine.StartPlanDocForTest();
            Assert.False(doc.Parked);
            Assert.True(doc.Decide.State == StartDecideState.Stable, doc.Decide.State + ": " + doc.Decide.Reason);
        }

        [Fact]
        public async Task The_real_scratch_carries_the_facts_of_the_sample_so_Jev_sees_before_the_build_what_the_Plan_sees_after_it()
        {
            await StartAsync();
            var doc = Engine.StartPlanDocForTest();
            var sample = new SourceSampleResult
            {
                Server = "contoso-sql.example.com", Database = "ContosoConsulting", MeasuredAtUtc = DateTime.UtcNow.ToString("o"), RowLimit = 20000,
                Tables = doc.Manifest.Tables.Select(t => new SourceTableFacts { Schema = t.SourceSchema, Name = t.SourceName, Rows = 1234 }).ToArray(),
            };
            var host = new EngineStartScratchHost(Engine);

            using (var none = await host.BuildAsync(doc, CancellationToken.None))
                Assert.Null(none.Facts);   // no sample read: nothing is invented

            Engine.SetStartPlanSample(doc.Id, sample);
            // THE CONSENT GATE (fix round after integration check 2): a sample the person has not allowed Jev to see stays out of what the rounds look at. With no
            // stored grant that named the sample, the scratch carries no facts at all.
            using (var shut = await host.BuildAsync(Engine.StartPlanDocForTest(), CancellationToken.None))
                Assert.Null(shut.Facts);
            // The person gives the model's permission and the grant names the sample (the test stands in for both, as StartPlanDocForTestMutate says): now it is in.
            await Engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            Engine.StartPlanDocForTestMutate(d => d.Decide = new StartDecide { State = StartDecideState.Deciding });
            doc = Engine.StartPlanDocForTest();
            using var scratch = await host.BuildAsync(doc, CancellationToken.None);
            var facts = scratch.Facts?.Invoke();
            Assert.NotNull(facts);
            Assert.Equal(ModelFactsData.SourceSample, facts.Data);
            Assert.All(facts.Tables, t => Assert.Equal(1234L, t.Rows));
            Assert.NotNull(scratch.ExecutorFacts?.Invoke());
            // And a plan that has a sample in use but a grant that says no sample is barred: the sample is part of what the person agreed to.
            Assert.Contains("switched on after you agreed", StartPlanDecide.WhyNoAsk(StartWithDisclosure(doc, sampleFacts: false), true));
        }

        private static StartPlanDoc StartWithDisclosure(StartPlanDoc doc, bool sampleFacts)
        {
            doc.Decide.State = StartDecideState.Deciding;
            doc.Disclosure = StartPlanDecide.DisclosureFor(doc, sampleFacts);
            return doc;
        }
    }
}
