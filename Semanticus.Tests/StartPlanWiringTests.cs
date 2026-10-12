// The wiring step of the spec-first start (m11/sf-int, after the twelve lanes): the seams the lanes left, each proved by a test that was RED first.
//
//   W1  the sample setting is part of what the person agrees to: it is in the digest and in the view, a grant refuses a plan whose sample moved,
//       and a cached answer is never served outside the scope.
//   W6  nothing removes a line a hand made: words, a pick or a pin on a table, a column, a measure, a link or a pick that goes out or is replaced is
//       HELD (kept, shown, not built), through ONE function, and comes back with its object.
//   W7  the state is true: a refused kick says waiting_grant with its reason, and the wait ends when nothing is in its way any more.
//
// Pure tests drive the functions on a document; engine tests drive the real LocalEngine on a scratch home (no network, the offline schema seam).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class StartPlanHoldPureTests
    {
        private static StartPlanDoc J8() => StartPlanDecideTests.DocFor(SqlDraftBuildTests.J8Schema());

        private static StartLine Hand(string by, string kind, string objectRef, string target, string after, string word = null) => new StartLine
        {
            Item = new ChangeItem { Kind = kind, ObjectRef = objectRef, ObjectName = objectRef.Substring(objectRef.LastIndexOf('/') + 1), Target = target, After = after, Source = by, Status = "approved" },
            DecidedBy = by, Word = word, Kept = true,
        };

        private static string MeasureRefOf(StartPlanDoc d, string table, string column)
        {
            var b = d.Manifest.BaseMeasures.Single(x => x.Table == table && x.Column == column);
            return "measure:" + b.Table + "/" + b.Name;
        }

        private static StartEditOutcome Edit(StartPlanDoc d, string op, string target, string value, string origin = "human")
        {
            var o = StartPlanEditing.Apply(d, new StartPlanEdit { Op = op, Target = target, Value = value }, origin);
            Assert.True(o.Ok, o.Refused);
            return o;
        }

        // ------------------------------------------------------------------ a column left out

        [Fact]
        public void Leaving_a_column_out_holds_the_words_and_the_choices_on_its_base_measure_and_the_measure_stays_in_the_manifest()
        {
            var doc = J8();
            var measureRef = MeasureRefOf(doc, "fact_invoice", "INV_AMT");
            var words = Hand(StartDecidedBy.Assistant, "set_description", measureRef, "description", "How much was invoiced.", "How much was invoiced.");
            var format = Hand(StartDecidedBy.Person, "set_measure_format", measureRef, "formatstring", "#,0.00");
            var loops = Hand(StartDecidedBy.Jev, "set_display_folder", measureRef, "displayfolder", "Money");
            doc.Lines.AddRange(new[] { words, format, loops });

            var o = Edit(doc, "include_column", "fact_invoice/INV_AMT", "false");

            Assert.Contains(words, doc.Lines);     // held, never deleted
            Assert.Contains(format, doc.Lines);
            Assert.NotNull(words.Held);
            Assert.NotNull(format.Held);
            Assert.DoesNotContain(loops, doc.Lines);   // the loop's own line is removed: the loop proposes it again when the measure returns
            Assert.Contains(o.KnockOns, k => k.Action == "held" && k.Ref == measureRef && k.Key.StartsWith("set_description"));
            Assert.Contains(o.KnockOns, k => k.Action == "held" && k.Ref == measureRef && k.Key.StartsWith("set_measure_format"));
            Assert.Contains(o.KnockOns, k => k.Action == "removed" && k.Ref == measureRef && k.Key.StartsWith("set_display_folder"));
            var bm = doc.Manifest.BaseMeasures.Single(b => b.Column == "INV_AMT");     // the measure itself is KEPT in the manifest
            Assert.False(doc.Manifest.IsBuilt(bm));
            Assert.DoesNotContain(doc.Manifest.ToSpec().Measures, m => m.Name == bm.Name);   // and builds nothing while its column is out

            Edit(doc, "include_column", "fact_invoice/INV_AMT", "true");

            Assert.Null(words.Held);               // it came back with its object
            Assert.Null(format.Held);
            Assert.True(doc.Manifest.IsBuilt(bm));
            Assert.Contains(doc.Manifest.ToSpec().Measures, m => m.Name == bm.Name);
            Assert.Equal(measureRef, "measure:" + bm.Table + "/" + bm.Name);
        }

        [Fact]
        public void A_persons_own_pick_survives_the_column_being_left_out_and_put_back()
        {
            var doc = J8();
            Edit(doc, "set_pick", "fact_invoice/INV_AMT", "average");
            var bm = doc.Manifest.BaseMeasures.Single(b => b.Column == "INV_AMT");
            Assert.Equal(("average", StartDecidedBy.Person), (bm.Pick, bm.DecidedBy));

            Edit(doc, "include_column", "fact_invoice/INV_AMT", "false");
            Assert.Contains(bm, doc.Manifest.BaseMeasures);           // the person's pick is not removed
            Edit(doc, "include_column", "fact_invoice/INV_AMT", "true");

            var back = doc.Manifest.BaseMeasures.Single(b => b.Column == "INV_AMT");
            Assert.Equal(("average", StartDecidedBy.Person), (back.Pick, back.DecidedBy));
            Assert.Contains(doc.Manifest.ToSpec().Measures, m => m.Name == back.Name);
        }

        // ------------------------------------------------------------------ a table left out

        [Fact]
        public void Leaving_a_table_out_holds_a_persons_pick_a_pin_and_the_lines_on_its_links_and_all_of_it_comes_back()
        {
            var doc = J8();
            var link = doc.Manifest.Links.Single(l => l.FromTable == "fact_timesheet" && l.ToTable == "dim_staff");
            var onLink = Hand(StartDecidedBy.Person, "set_relationship_crossfilter", "relationship:" + link.Identity, "crossfilter", "Both");
            var onColumn = Hand(StartDecidedBy.Person, "set_description", "column:dim_staff/EMP_NM", "description", "The person's own words.");
            var assistant = Hand(StartDecidedBy.Assistant, "set_description", "table:dim_staff", "description", "One row per staff member.", "One row per staff member.");
            doc.Lines.AddRange(new[] { onLink, onColumn, assistant });
            Edit(doc, "set_pick", "dim_staff/STD_RATE", "average");   // the person's pick on a column of the table that is about to go

            var o = Edit(doc, "include_table", "dim_staff", "false");

            Assert.All(new[] { onLink, onColumn, assistant }, l => { Assert.Contains(l, doc.Lines); Assert.NotNull(l.Held); });
            Assert.Contains(o.KnockOns, k => k.Action == "held" && k.Ref == "relationship:" + link.Identity);
            var pick = doc.Manifest.BaseMeasures.Single(b => b.Table == "dim_staff" && b.Column == "STD_RATE");
            Assert.Equal(StartDecidedBy.Person, pick.DecidedBy);
            Assert.False(doc.Manifest.IsBuilt(pick));

            Edit(doc, "include_table", "dim_staff", "true");

            Assert.All(new[] { onLink, onColumn, assistant }, l => Assert.Null(l.Held));
            Assert.True(doc.Manifest.IsBuilt(pick));
            Assert.Equal("average", pick.Pick);
        }

        [Fact]
        public void A_link_left_out_by_leaving_out_one_of_its_columns_holds_the_lines_on_that_link()
        {
            var doc = J8();
            var link = doc.Manifest.Links.Single(l => l.FromTable == "fact_timesheet" && l.ToTable == "dim_project");
            var onLink = Hand(StartDecidedBy.Person, "set_relationship_crossfilter", "relationship:" + link.Identity, "crossfilter", "Both");
            doc.Lines.Add(onLink);

            Edit(doc, "include_column", "fact_timesheet/PRJ_ID", "false");
            Assert.Contains(onLink, doc.Lines);
            Assert.NotNull(onLink.Held);

            Edit(doc, "include_column", "fact_timesheet/PRJ_ID", "true");
            Assert.Null(onLink.Held);
        }

        // ------------------------------------------------------------------ a pick replaced

        [Fact]
        public void A_pick_replaced_by_the_person_holds_the_words_on_the_measure_it_replaces_and_they_return_when_the_pick_goes_back()
        {
            var doc = J8();
            var was = doc.Manifest.BaseMeasures.Single(b => b.Column == "INV_AMT");
            var oldName = was.Name;
            var oldRef = "measure:" + was.Table + "/" + oldName;
            var words = Hand(StartDecidedBy.Assistant, "set_description", oldRef, "description", "How much was invoiced.", "How much was invoiced.");
            doc.Lines.Add(words);

            var o = Edit(doc, "set_pick", "fact_invoice/INV_AMT", "average");

            Assert.NotEqual(oldName, was.Name);
            Assert.Contains(words, doc.Lines);
            Assert.NotNull(words.Held);
            Assert.Contains(o.KnockOns, k => k.Action == "held" && k.Ref == oldRef);

            Edit(doc, "set_pick", "fact_invoice/INV_AMT", "sum");

            Assert.Equal(oldName, was.Name);
            Assert.Null(words.Held);   // the measure it was about is back
        }

        // ------------------------------------------------------------------ what a round does (the other path that used to delete)

        [Fact]
        public void Settle_holds_a_hand_made_line_whose_object_is_out_by_choice_lets_a_loop_line_go_and_releases_a_hold_when_the_object_is_back()
        {
            var doc = J8();
            var measureRef = MeasureRefOf(doc, "fact_invoice", "INV_AMT");
            Edit(doc, "include_column", "fact_invoice/INV_AMT", "false");

            var handMade = Hand(StartDecidedBy.Person, "set_description", measureRef, "description", "mine");
            var loop = Hand(StartDecidedBy.Jev, "set_description", measureRef, "description", "jev's");
            Assert.Equal(StartPlanHold.Fate.Held, StartPlanHold.Settle(doc, handMade, r => false, null));
            Assert.NotNull(handMade.Held);
            Assert.Equal(StartPlanHold.Fate.Goes, StartPlanHold.Settle(doc, loop, r => false, null));

            // A hand-made line whose object is gone for ANOTHER reason (a measure the loop re-picked) is not out by choice: it goes, as before.
            var gone = Hand(StartDecidedBy.Person, "set_description", "measure:fact_invoice/A measure nobody makes", "description", "x");
            Assert.Equal(StartPlanHold.Fate.Goes, StartPlanHold.Settle(doc, gone, r => false, null));

            // A hold an edit made stays while the scratch cannot find the object, and is released the moment it can.
            var held = Hand(StartDecidedBy.Assistant, "set_description", "measure:fact_invoice/A measure nobody makes", "description", "x", "x");
            held.Held = "Held, because the measure was replaced.";
            Assert.Equal(StartPlanHold.Fate.Held, StartPlanHold.Settle(doc, held, r => false, null));
            Assert.Equal(StartPlanHold.Fate.Stands, StartPlanHold.Settle(doc, held, r => true, null));
            Assert.Null(held.Held);
        }

        // ------------------------------------------------------------------ the class: every hand-made line, every path

        [Theory]
        [InlineData("include_table", "dim_staff", "false", "table:dim_staff")]
        [InlineData("include_column", "dim_staff/EMP_NM", "false", "column:dim_staff/EMP_NM")]
        public void No_edit_that_leaves_something_out_removes_a_line_a_hand_made(string op, string target, string value, string objectRef)
        {
            var doc = J8();
            var mine = new[]
            {
                Hand(StartDecidedBy.Person, "set_description", objectRef, "description", "person"),
                Hand(StartDecidedBy.Assistant, "set_synonyms", objectRef, "synonyms", "assistant", "assistant"),
            };
            doc.Lines.AddRange(mine);
            var before = doc.Lines.Count;

            Edit(doc, op, target, value);

            Assert.Equal(before, doc.Lines.Count);
            Assert.All(mine, l => Assert.NotNull(l.Held));
        }
    }

    /// <summary>The same class of loss, through ROUNDS (the other path that used to delete): the real loop runs while the object is out, and the hand-made lines are
    /// still there, held, and land again when the object is back.</summary>
    public sealed class StartPlanHoldRoundTests
    {
        private static StartLine Hand(string by, string kind, string objectRef, string target, string after, string word = null) => new StartLine
        {
            Item = new ChangeItem { Kind = kind, ObjectRef = objectRef, ObjectName = objectRef.Substring(objectRef.LastIndexOf('/') + 1), Target = target, After = after, Source = by, Status = "approved" },
            DecidedBy = by, Word = word, Kept = true, Round = 1,
        };

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_round_run_while_a_column_is_out_or_a_pick_is_replaced_keeps_every_hand_made_line_and_they_return_with_their_object()
        {
            var oracle = new StartPlanDecideTests.PolicyOracle();
            using var rig = await StartPlanDecideTests.Rig.CreateAsync(SqlDraftBuildTests.J8Schema(), oracle);
            Assert.True(await rig.DecideAsync());
            var doc = rig.Doc;
            var bm = doc.Manifest.BaseMeasures.Single(b => b.Table == "fact_invoice" && b.Column == "INV_AMT");
            var oldRef = "measure:" + bm.Table + "/" + bm.Name;
            // the assistant's words over the engine's own description of the measure (what write_start_words does to the plan)
            var words = doc.Lines.Single(l => l.Item.ObjectRef == oldRef && l.Item.Kind == "set_description");
            words.Item.After = "Money invoiced."; words.DecidedBy = StartDecidedBy.Assistant; words.Word = "Money invoiced."; words.Kept = true; words.Item.Status = "approved";
            // a person's drop of a line Jev made (a format on a column): the line becomes the person's
            var jevFormat = doc.Lines.First(l => l.DecidedBy == StartDecidedBy.Jev && l.Item.ObjectRef.StartsWith("column:dim_", StringComparison.Ordinal) && !l.Item.ObjectRef.StartsWith("column:dim_date/", StringComparison.Ordinal));
            var pinnedRef = jevFormat.Item.ObjectRef;
            var pinnedTarget = pinnedRef.Substring("column:".Length);
            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "drop_line", Target = StartPlanDecide.LineKey(jevFormat) }, "human").Ok);
            var pinnedColumn = jevFormat;
            Assert.True(await rig.DecideAsync());
            Assert.Null(words.Held); Assert.Null(pinnedColumn.Held);

            // a column out (the base measure's, and another with a person's line on it)
            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "include_column", Target = "fact_invoice/INV_AMT", Value = "false" }, "human").Ok);
            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "include_column", Target = pinnedTarget, Value = "false" }, "human").Ok);
            Assert.True(await rig.DecideAsync());
            Assert.Contains(words, doc.Lines); Assert.Contains(pinnedColumn, doc.Lines);
            Assert.NotNull(words.Held); Assert.NotNull(pinnedColumn.Held);

            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "include_column", Target = "fact_invoice/INV_AMT", Value = "true" }, "human").Ok);
            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "include_column", Target = pinnedTarget, Value = "true" }, "human").Ok);
            Assert.True(await rig.DecideAsync());
            Assert.Contains(doc.Lines, l => l.Word == "Money invoiced." && l.Held == null && l.Kept);
            Assert.Contains(doc.Lines, l => l.Item.ObjectRef == pinnedRef && l.DecidedBy == StartDecidedBy.Person && l.Held == null);

            // a pick replaced by the person: the words on the measure it replaces are held through a round, and come back when the pick does
            var nameBefore = doc.Manifest.BaseMeasures.Single(b => b.Column == "INV_AMT").Name;
            var edit = StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "set_pick", Target = "fact_invoice/INV_AMT", Value = "average" }, "human");
            Assert.True(edit.Ok);
            var heldAfterEdit = doc.Lines.Single(l => l.Word == "Money invoiced.").Held;
            string scratchMeasures;
            using (var probe = await rig.Host.BuildAsync(doc, CancellationToken.None))
                scratchMeasures = string.Join(",", await probe.Session.ReadAsync(m => m.AllMeasures.Where(x => x.Table.Name == "fact_invoice").Select(x => x.Name).ToList()));
            Assert.True(await rig.DecideAsync());
            var held = doc.Lines.Single(l => l.Word == "Money invoiced.");
            Assert.True(held.Held != null, "ref " + held.Item.ObjectRef + " bases: " + string.Join("; ", doc.Manifest.BaseMeasures.Where(b => b.Column == "INV_AMT").Select(b => b.Name + "/" + b.Pick + "/" + b.DecidedBy)) + " oldRef " + oldRef + " nameBefore " + nameBefore + " heldAfterEdit " + (heldAfterEdit ?? "(null)") + " scratch " + scratchMeasures + " knock " + string.Join(";", edit.KnockOns.Select(k => k.Action + k.Ref)) + " lines " + string.Join(" | ", doc.Lines.Select(l => l.Item.ObjectRef + "/" + l.Item.Kind + "/" + l.DecidedBy)));
            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "set_pick", Target = "fact_invoice/INV_AMT", Value = "sum" }, "human").Ok);
            Assert.True(await rig.DecideAsync());
            Assert.Null(doc.Lines.Single(l => l.Word == "Money invoiced.").Held);
        }
    }

    /// <summary>The engine's own state words are true (W7), and the grant carries the sample setting the person saw (W1), on the real engine.</summary>
    [Collection("restore-root")]
    public sealed class StartPlanWiringEngineTests : IDisposable
    {
        private readonly string _root;
        private readonly string _origHome;
        private readonly DecisionFixture _f;

        public StartPlanWiringEngineTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "sw-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(_root, "home", ".semanticus"));
            _origHome = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, Path.Combine(_root, "home", ".semanticus"));
            _f = new DecisionFixture(TestEntitlements.Pro);
            _f.Engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(SqlDraftBuildTests.J8Schema());
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, _origHome);
            _f.Dispose();
            try { Directory.Delete(_root, true); } catch { }
        }

        private LocalEngine Engine => _f.Engine;

        private async Task<StartPlanView> StartAsync(bool consent = true)
        {
            var view = await Engine.StartModelFromSqlAsync(new StartModelRequest
            {
                Server = "contoso-sql.example.com", Database = "ContosoConsulting", Name = "Uplift",
                Folder = Path.Combine(_root, "dest-" + Guid.NewGuid().ToString("N").Substring(0, 6)),
            }, "human");
            if (consent) await _f.ConsentAsync();
            return view;
        }

        private static string[] SourceTables(StartPlanDoc d) => d.Manifest.Tables.Where(t => t.Include).Select(StartPlanDecide.SourceNameOf).ToArray();

        [Fact]
        public async Task Bulk_start_choices_write_once_keep_good_edits_and_name_a_bad_target()
        {
            Engine.StartDecideKickDisabledForTest = true;
            var before = await StartAsync(consent: false);
            var doc = StartPlanStore.Read(before.Id);
            var tables = doc.Manifest.Tables.Where(t => t.Include).Take(2).ToArray();
            Assert.Equal(2, tables.Length);
            var announced = 0;
            _f.Sessions.Bus.StartPlanChanged += _ => announced++;
            var result = await Engine.EditStartPlansAsync(new[] {
                new StartPlanEdit { Op = "include_table", Target = tables[0].Name, Value = "false" },
                new StartPlanEdit { Op = "include_table", Target = "missing-table", Value = "false" },
                new StartPlanEdit { Op = "include_table", Target = tables[1].Name, Value = "false" },
            }, "human");
            Assert.Equal(2, result.Applied);
            Assert.Single(result.Rejected);
            Assert.Equal("missing-table", result.Rejected[0].ObjRef);
            var after = StartPlanStore.Read(before.Id);
            Assert.Equal(doc.Version + 1, after.Version);
            Assert.False(after.Manifest.Tables.Single(t => t.Name == tables[0].Name).Include);
            Assert.False(after.Manifest.Tables.Single(t => t.Name == tables[1].Name).Include);
            Assert.Equal(1, announced);
            Assert.Null(after.Approval);
            var noop = await Engine.EditStartPlansAsync(new[] {
                new StartPlanEdit { Op = "include_table", Target = tables[0].Name, Value = "false" },
            }, "human");
            Assert.Equal(0, noop.Applied);
            Assert.Equal(after.Version, StartPlanStore.Read(before.Id).Version);
            Assert.Equal(1, announced);
        }

        // ------------------------------------------------------------------ W1: the sample setting is part of what was agreed

        [Fact]
        public async Task The_digest_and_the_view_carry_the_sample_flag_so_a_sample_read_after_the_person_looked_moves_both()
        {
            var seen = await StartAsync();
            Assert.False(seen.SampleFacts);

            Engine.NoteStartSampleRead(seen.Id, true);
            var after = await Engine.GetStartPlanAsync();

            Assert.True(after.SampleFacts);
            Assert.NotEqual(seen.Digest, after.Digest);   // the flag is in the digest
            Assert.Equal(StartSampleStatus.Read, after.Sample.State);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_grant_is_refused_while_the_sample_is_still_being_read_and_covers_the_sample_once_it_is_in_the_plan_the_person_sees()
        {
            var seen = await StartAsync();
            await Engine.StartPlanHost.UpdateAsync(d => { d.Sample = new StartSampleStatus { State = StartSampleStatus.Reading }; return true; });
            var reading = await Engine.GetStartPlanAsync();
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Engine.GrantStartDisclosureAsync(reading.Digest, "human"));
            Assert.Contains("sample", refused.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Null(Engine.StartPlanDocForTest().Disclosure);

            Engine.NoteStartSampleRead(seen.Id, true);   // the read lands: the plan now says a sample is on
            var withSample = await Engine.GetStartPlanAsync();
            Assert.True(withSample.SampleFacts);
            var granted = await Engine.GrantStartDisclosureAsync(withSample.Digest, "human");

            Assert.True(granted.Exists);
            Assert.True(Engine.StartPlanDocForTest().Disclosure.SampleFacts);   // the person saw a sample in the plan, and agreed to it
        }

        // A cached answer is never served outside the scope (the first Refuse check, before the cache): an answer bought while the plan disclosed one thing is not
        // an answer for a plan that discloses another. Removing the check in DecisionService turns this red (shown by hand in the report).
        private async Task<DecisionOutcome> AskTheSameAsync(bool sampleFacts)
        {
            var doc = Engine.StartPlanHost.Current();
            var ask = StartAskScope.For(doc, new[] { "dim_staff" }, sampleFacts);
            var fence = await Engine.StartAskFenceAsync("scratch-w", 1, () => ("scratch-w", 1), ask);
            var point = DecisionRegistry.Require(DecisionRegistry.MeasureFolderAndFormat);
            var snapshot = DecisionSnapshot.Build(point, new[]
            {
                new KeyValuePair<string, object>("measure_name", "Total Rate"),
                new KeyValuePair<string, object>("measure_formula_facts", DecisionFixture.SampleFormulaFacts("SUM")),
                new KeyValuePair<string, object>("table_name", "dim_staff"),
                new KeyValuePair<string, object>("table_columns", new List<object> { new Dictionary<string, object> { { "name", "STD_RATE" }, { "data_type", "Double" } } }),
                new KeyValuePair<string, object>("sibling_measures", new List<object>()),
            });
            return await Engine.Decisions.AskAsync(point.Name, snapshot, MeasureDefaultsPoint.Questions(new[] { "Volume" }, true, true), isPro: true, enabled: true, fence: fence, ct: CancellationToken.None);
        }

        [Fact]
        public async Task A_cached_answer_is_not_served_once_the_scope_it_was_bought_under_has_narrowed_or_moved()
        {
            Engine.StartDecideKickDisabledForTest = true;
            var v = await StartAsync();
            await Engine.GrantStartDisclosureAsync(v.Digest, "human");

            // Record what an oracle says to this one request (the cache is read only for an oracle that names the version it served), then replay it.
            var policy = new StartPlanDecideTests.PolicyOracle();
            Engine.UseDecisionsForTest(new DecisionService(policy));
            Assert.True((await AskTheSameAsync(false)).Asked);
            var oracle = new RecordedDecisionOracle(policy.Fixture(), "jev-test-1");
            Engine.UseDecisionsForTest(new DecisionService(oracle));

            var first = await AskTheSameAsync(false);
            Assert.True(first.Asked, first.SkippedReason);
            Assert.Single(oracle.AskedDigests);
            var again = await AskTheSameAsync(false);
            Assert.True(again.Answers != null && oracle.AskedDigests.Count == 1, "the cache answers the same ask inside the scope");

            Engine.NoteStartSampleRead(v.Id, true);   // the plan now discloses facts from a sample, which the grant did not name
            var outside = await AskTheSameAsync(true);
            Assert.Null(outside.Answers);
            Assert.Contains("nothing was sent", outside.SkippedReason);
            var stale = await AskTheSameAsync(false);   // even the ask that was answered before: the plan is no longer the plan that was granted
            Assert.Null(stale.Answers);
            Assert.Single(oracle.AskedDigests);
        }

        // ------------------------------------------------------------------ W4: both doors, the digest, the decline

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Approve_and_build_refuse_a_digest_that_is_not_the_current_one_on_both_doors()
        {
            var v = await StartAsync();
            var stale = new string('0', v.Digest.Length);
            var human = new EngineRpcTarget(Engine);
            var studio = new EngineRpcTarget(Engine, "human");
            var agent = new EngineRpcTarget(Engine, "agent");

            await Assert.ThrowsAsync<InvalidOperationException>(() => human.approveStartPlan(stale));   // the person's door
            Assert.False((await McpTools.BuildStartPlan(Engine, stale)).Built);                       // the assistant's tool
            Assert.False((await agent.buildStartPlan(stale)).Built);                                  // the pipe, as the assistant's connection
            Assert.False((await studio.buildStartPlan(stale)).Built);                                 // the pipe, as the Studio

            // An approval of the current plan, then an edit: the OLD digest, which the approval was made on, builds nothing either way.
            await human.approveStartPlan(v.Digest);
            var moved = await McpTools.EditStartPlan(Engine, "include_table", "ref_codes", "false");
            Assert.NotEqual(v.Digest, moved.Digest);
            Assert.Null(moved.Approval);
            var refused = await McpTools.BuildStartPlan(Engine, v.Digest);
            Assert.False(refused.Built);
            Assert.Contains("approve", refused.Refused, StringComparison.OrdinalIgnoreCase);
            Assert.False((await agent.buildStartPlan(v.Digest)).Built);
            await Assert.ThrowsAsync<InvalidOperationException>(() => human.approveStartPlan(v.Digest));
        }

        [Fact]
        public async Task Not_now_on_the_grant_carries_the_plan_on_without_Jev_and_it_can_be_allowed_later_on_either_door()
        {
            var v = await StartAsync();
            var oracle = new VerifyTextOracle();
            Engine.UseDecisionsForTest(new DecisionService(oracle));
            Assert.Equal(StartDecideState.WaitingGrant, v.Decide.State);

            var declined = await McpTools.EditStartPlan(Engine, "answer", "grant", "Not now");   // the assistant's door (the Studio's is the same verb)
            Assert.True(declined.JevDeclined);
            Assert.Equal(StartDecideState.Off, declined.Decide.State);
            Assert.DoesNotContain(declined.NeedsYou, n => n.Kind == "grant");
            Assert.Contains(declined.NeedsYou, n => n.Kind == "grant_declined");
            Assert.Equal(0, oracle.Asks);
            await Engine.KickStartDecideOnPlanAsync();
            await Engine.StartDecideSettledForTest();
            Assert.Equal(0, oracle.Asks);   // nothing is asked while declined, not even by a kick

            // Allow later: the grant clears the decline and Jev takes the plan up.
            var allowed = await Engine.GrantStartDisclosureAsync(declined.Digest, "human");
            Assert.False(allowed.JevDeclined);
            await Engine.StartDecideSettledForTest();
            Assert.True(oracle.Asks > 0);
            Assert.NotEqual(StartDecideState.Off, Engine.StartPlanDocForTest().Decide.State);
        }

        [Fact]
        public async Task A_sample_the_source_refuses_is_said_in_plain_words_in_Needs_you_and_the_plan_is_made_from_names_alone()
        {
            // The sample step runs when a test gives it its own executor; the J8 source's host is not a Fabric endpoint, so the read is refused with 0 queries.
            var queries = 0;
            Engine.SourceSampleExecutorForTest = (server, db, sql, max) => { queries++; return Task.FromResult(ResultSet.FromError("no")); };
            var view = await StartAsync(consent: false);
            await Engine.StartSampleSettledForTest();
            var after = await Engine.GetStartPlanAsync();

            Assert.False(after.SampleFacts);
            Assert.Equal(StartSampleStatus.Refused, after.Sample.State);
            var need = Assert.Single(after.NeedsYou, n => n.Kind == "sample_refused");
            Assert.Contains("names alone", need.Text);
            Assert.DoesNotContain("—", need.Text);
            Assert.Equal(0, queries);
            _ = view;
        }

        /// <summary>The loop uses sf-consent's REAL fence (the request's own text is read, not what the asker declares). A column the person renamed with the name of a table
        /// they left out would otherwise be sent to Jev inside an ask about a table that is in. Removing the fence from the loop turns this red.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_decide_loops_real_fence_never_lets_the_name_of_a_table_the_person_left_out_reach_Jev()
        {
            await StartAsync();
            var oracle = new VerifyTextOracle();
            Engine.UseDecisionsForTest(new DecisionService(oracle));
            Assert.True((await Engine.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = "dim_staff", Value = "false" }, "human")).Exists);
            await Engine.EditStartPlanAsync(new StartPlanEdit { Op = "rename", Target = "column:dim_client/CLNT_NM", Value = "dim_staff notes" }, "human");
            await Engine.GrantStartDisclosureAsync((await Engine.GetStartPlanAsync()).Digest, "human");
            await Engine.StartDecideSettledForTest();

            Assert.True(oracle.Asks > 0, "the asks that do not name it must still go");
            Assert.DoesNotContain(oracle.Texts, t => t.IndexOf("dim_staff", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // ------------------------------------------------------------------ W7: the state is true

        [Fact]
        public async Task A_kick_the_bar_refuses_says_waiting_for_the_grant_with_its_reason_and_not_deciding_with_no_loop_running()
        {
            await StartAsync();
            var oracle = new VerifyTextOracle();
            Engine.UseDecisionsForTest(new DecisionService(oracle));
            // The plan SAYS deciding but has no grant that covers it (a plan the engine met after a restart, or one an old build left).
            await Engine.StartPlanHost.UpdateAsync(d => { d.Decide = new StartDecide { State = StartDecideState.Deciding }; d.Disclosure = null; return true; });

            await Engine.KickStartDecideOnPlanAsync();
            await Engine.StartDecideSettledForTest();

            var doc = Engine.StartPlanDocForTest();
            Assert.Equal(StartDecideState.WaitingGrant, doc.Decide.State);
            Assert.False(string.IsNullOrWhiteSpace(doc.Decide.Reason));
            Assert.Equal(0, oracle.Asks);
        }

        [Fact]
        public async Task A_plan_the_unpark_would_restart_but_the_bar_refuses_says_waiting_for_the_grant_too()
        {
            await StartAsync();
            Engine.UseDecisionsForTest(new DecisionService(new VerifyTextOracle()));
            await Engine.StartPlanHost.UpdateAsync(d => { d.Decide = new StartDecide { State = StartDecideState.Deciding }; d.Disclosure = null; d.Parked = true; return true; });

            await Engine.GetStartPlanAsync();   // the model is open, so this read unparks it and would restart the loop
            await Engine.StartDecideSettledForTest();

            var doc = Engine.StartPlanDocForTest();
            Assert.False(doc.Parked);
            Assert.Equal(StartDecideState.WaitingGrant, doc.Decide.State);
            Assert.False(string.IsNullOrWhiteSpace(doc.Decide.Reason));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_wait_for_a_grant_ends_when_the_extra_table_is_taken_back_out_with_no_new_grant()
        {
            await StartAsync();
            var oracle = new VerifyTextOracle();
            Engine.UseDecisionsForTest(new DecisionService(oracle));
            // The person leaves dim_staff out, then agrees: the grant names every other table.
            await Engine.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = "dim_staff", Value = "false" }, "human");
            await Engine.GrantStartDisclosureAsync((await Engine.GetStartPlanAsync()).Digest, "human");
            await Engine.StartDecideSettledForTest();
            Assert.True(oracle.Asks > 0);

            // Putting it back is a table the grant never named: the plan waits, and says which.
            await Engine.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = "dim_staff", Value = "true" }, "human");
            await Engine.StartDecideSettledForTest();
            var waiting = Engine.StartPlanDocForTest();
            Assert.Equal(StartDecideState.WaitingGrant, waiting.Decide.State);
            Assert.Contains("dim_staff", waiting.Decide.Reason);

            // Taking it back out again leaves the wait by itself: nothing new is being shown, so the grant that stands covers the plan.
            var asksBefore = oracle.Asks;
            await Engine.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = "dim_staff", Value = "false" }, "human");
            await Engine.StartDecideSettledForTest();
            var clear = Engine.StartPlanDocForTest();
            Assert.True(clear.Decide.State == StartDecideState.Stable || clear.Decide.State == StartDecideState.Incomplete, clear.Decide.State + ": " + clear.Decide.Reason);
            Assert.True(oracle.Asks >= asksBefore);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_wait_ends_when_a_decision_point_that_was_off_is_switched_on_again_with_the_grant_still_standing()
        {
            await StartAsync();
            var oracle = new VerifyTextOracle();
            Engine.UseDecisionsForTest(new DecisionService(oracle));
            await Engine.GrantStartDisclosureAsync((await Engine.GetStartPlanAsync()).Digest, "human");
            await Engine.StartDecideSettledForTest();
            // The loop ended waiting because no point was live (the shape Finish(WaitingGrant) leaves), the grant itself untouched.
            await Engine.StartPlanHost.UpdateAsync(d => { d.Decide.State = StartDecideState.WaitingGrant; d.Decide.Reason = StartDecideCopy.WaitingGrantReason; return true; });
            var asks = oracle.Asks;

            await Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnFormat, true, "human");
            // The look at the plan runs off the call's path: wait for it (a few seconds at most), then for the loop it starts.
            for (var i = 0; i < 100 && Engine.StartPlanDocForTest().Decide.State == StartDecideState.WaitingGrant; i++) await Task.Delay(100);
            await Engine.StartDecideSettledForTest();

            Assert.NotEqual(StartDecideState.WaitingGrant, Engine.StartPlanDocForTest().Decide.State);
            Assert.True(oracle.Asks >= asks);
        }
    }

    /// <summary>The build's wiring (W3), on the real engine, on a Free plan (nothing here needs Jev): the plan is kept and marked built, an undone build builds again,
    /// what is held is skipped, the preview carries the Best practice letter, the digest covers a line's Args, and the person's declines travel into the Plan.</summary>
    [Collection("restore-root")]
    public sealed class StartPlanWiringBuildTests : IDisposable
    {
        private readonly string _root;
        private readonly string _origHome;
        private readonly DecisionFixture _f;

        public StartPlanWiringBuildTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "sb-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(_root, "home", ".semanticus"));
            _origHome = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, Path.Combine(_root, "home", ".semanticus"));
            _f = new DecisionFixture(TestEntitlements.Free);
            _f.Engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(SqlDraftBuildTests.J8Schema());
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, _origHome);
            _f.Dispose();
            try { Directory.Delete(_root, true); } catch { }
        }

        private LocalEngine Engine => _f.Engine;

        private async Task<StartPlanView> StartAsync()
        {
            var v = await Engine.StartModelFromSqlAsync(new StartModelRequest
            {
                Server = "contoso-sql.example.com", Database = "ContosoConsulting", Name = "Uplift",
                Folder = Path.Combine(_root, "dest-" + Guid.NewGuid().ToString("N").Substring(0, 6)),
            }, "human");
            await Engine.StartPreviewSettledForTest();
            return await Engine.GetStartPlanAsync();
        }

        private async Task<int> TablesAsync() => await _f.Sessions.Current.ReadAsync(m => m.Tables.Count);

        private async Task<StartPlanView> ApprovedAsync()
        {
            await Engine.StartPreviewSettledForTest();
            var v = await Engine.GetStartPlanAsync();
            return await Engine.ApproveStartPlanAsync(v.Digest);
        }

        private async Task WriteWordsAsync(string objectRef, string text)
        {
            var r = await Engine.WriteStartWordsAsync(new[] { new StartWord { Ref = objectRef, Kind = "description", Text = text } }, "agent");
            Assert.True(r.Written == 1, string.Join("; ", r.Refused));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_read_after_a_build_says_built_and_the_plan_is_kept_not_deleted()
        {
            var v = await StartAsync();
            Assert.True(v.Grades.AfterBuildGrade != null || v.Grades.AfterBuildBestPractice != null, "the preview was not made");
            v = await ApprovedAsync();
            var built = await Engine.BuildStartPlanAsync(v.Digest, "human");
            Assert.True(built.Built, built.Refused);

            Assert.True(built.View.Exists);          // never "no plan"
            Assert.True(built.View.Built);
            var read = await Engine.GetStartPlanAsync();
            Assert.True(read.Exists && read.Built && !read.CanBuild);
            Assert.Contains("built", read.BlockedBecause, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(StartPlanStore.Read(v.Id));   // the document is still on disk
            Assert.NotNull(StartPlanStore.Read(v.Id).BuiltUtc);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_build_that_was_undone_builds_again_with_the_words_and_the_approval_it_had()
        {
            var v = await StartAsync();
            await WriteWordsAsync("table:dim_staff", "One row per staff member.");
            v = await ApprovedAsync();
            Assert.True((await Engine.BuildStartPlanAsync(v.Digest, "human")).Built);
            Assert.True(await TablesAsync() > 0);

            await Engine.UndoAsync("human");
            Assert.Equal(0, await TablesAsync());

            var back = await Engine.GetStartPlanAsync();
            Assert.False(back.Built);
            Assert.Equal(v.Digest, back.Digest);                                 // the same plan
            Assert.Equal(v.Digest, back.Approval?.Digest);                        // its approval is not lost
            Assert.Contains(StartPlanStore.Read(v.Id).Lines, l => l.Word == "One row per staff member.");   // nor its words
            await Engine.StartPreviewSettledForTest();
            var again = await Engine.BuildStartPlanAsync(v.Digest, "human");
            Assert.True(again.Built, again.Refused);   // the Verified Edits record of the first build does not block the second
            Assert.True(await TablesAsync() > 0);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_edit_after_an_undone_build_keeps_the_words_and_builds_after_a_new_approval()
        {
            var v = await StartAsync();
            await WriteWordsAsync("table:dim_staff", "One row per staff member.");
            v = await ApprovedAsync();
            Assert.True((await Engine.BuildStartPlanAsync(v.Digest, "human")).Built);
            await Engine.UndoAsync("human");

            var edited = await Engine.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = "ref_codes", Value = "false" }, "human");
            Assert.Null(edited.Approval);   // any edit voids the approval
            Assert.Contains(StartPlanStore.Read(v.Id).Lines, l => l.Word == "One row per staff member.");
            v = await ApprovedAsync();
            var built = await Engine.BuildStartPlanAsync(v.Digest, "human");
            Assert.True(built.Built, built.Refused);
            var names1 = await _f.Sessions.Current.ReadAsync(m => m.Tables.Select(t => t.Name).ToList());
            Assert.DoesNotContain("ref_codes", names1);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_build_skips_a_held_line_instead_of_refusing_and_the_line_waits_on_the_plan()
        {
            var v = await StartAsync();
            await WriteWordsAsync("column:dim_staff/EMP_NM", "The staff member's full name.");
            await Engine.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = "dim_staff", Value = "false" }, "human");
            v = await ApprovedAsync();

            var built = await Engine.BuildStartPlanAsync(v.Digest, "human");

            Assert.True(built.Built, built.Refused);
            var names2 = await _f.Sessions.Current.ReadAsync(m => m.Tables.Select(t => t.Name).ToList());
            Assert.DoesNotContain("dim_staff", names2);
            var held = StartPlanStore.Read(v.Id).Lines.Single(l => l.Word != null);
            Assert.NotNull(held.Held);
        }

        [Fact]
        public async Task The_preview_carries_the_best_practice_letter_and_the_view_shows_it()
        {
            var v = await StartAsync();
            Assert.Matches("^[A-F]$", v.Grades.AfterBuildBestPractice ?? "");
            Assert.Matches("^[A-F]$", v.Grades.AfterBuildGrade ?? "");
            Assert.False(v.Grades.AfterBuildProvisional);   // a plan Jev does not decide is settled
            var stored = Engine.StartPlanDocForTest().Preview;
            Assert.Equal(v.Grades.AfterBuildBestPractice, stored.BestPractice);
        }

        [Fact]
        public void The_approval_digest_covers_a_lines_Args_because_the_executor_writes_from_them()
        {
            var doc = StartPlanDecideTests.DocFor(SqlDraftBuildTests.J8Schema());
            doc.Lines.Add(new StartLine { DecidedBy = StartDecidedBy.Person, Kept = true, Item = new ChangeItem { Kind = "set_synonyms", ObjectRef = "column:dim_staff/EMP_NM", Target = "synonyms", After = "a, b", Args = new Dictionary<string, string> { ["replacedList"] = "x" } } });
            var one = StartPlanViewBuilder.Digest(doc);
            doc.Lines[0].Item.Args["replacedList"] = "y";
            Assert.NotEqual(one, StartPlanViewBuilder.Digest(doc));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_getStartWords_page_is_clamped_in_the_engine_whatever_a_door_sends()
        {
            await StartAsync();
            var big = await Engine.GetStartWordsAsync(100000, -7);
            Assert.True(big.Items.Count <= 100);
            Assert.Equal(0, big.Offset);
            var past = await Engine.GetStartWordsAsync(-3, 1000000);
            Assert.Equal(past.Total, past.Offset);
            Assert.Empty(past.Items);
            var zero = await Engine.GetStartWordsAsync(0, 0);
            Assert.InRange(zero.Items.Count, 1, 40);
        }
    }
}
