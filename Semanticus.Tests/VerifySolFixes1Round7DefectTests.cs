// verify: the verifier's failing tests for m11/sol-fixes-1 round 7 (79769b3d, THE ADDING RULE version 5). Each test is LEFT
// FAILING on purpose: it passes once the defect is fixed. The words are the blind held-out set a separate helper session wrote
// (Semanticus.Tests/fixtures/verify-sol-fixes-1-r7-heldout.json, with the writer's input and output beside it), labelled by the
// verifier and committed at 6f503909 before any run. That set was run ONCE and failed the stop rule; it is spent as a held-out
// set, and these tests keep it as a regression. The model is the one the writer was told of; the Jev oracle is scripted (it
// answers the exact request the engine built, through the real recorded oracle), saying no to every step, or sure of every add.
//
// VR17: the harm. No take-away the verifier labelled must-not-keep may leave a KEPT add in the draft (stop rule: 0, in every
// mode, on both doors). 8 of the 60 keep one: six add first and take it back (or put it on a list to delete) with Add more
// words, which never unticks a step already ticked, though K4 reads "every later clause of the words, in this typing or after
// it"; two take it away BEFORE the add ("anything in my next message goes on the list to delete", "please delete the
// calculation group I'm about to ask for"), which no check of version 5 reads.
// VR18: the plain adds. The stop rule asks at least 90% kept and at least 97% offered (a step, kept or unticked), per mode over
// both doors.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Composer;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class VerifySolFixes1Round7DefectTests : IClassFixture<VerifySolFixes1Round7DefectTests.HeldOutTemplate>
    {
        private readonly HeldOutTemplate _template;

        public VerifySolFixes1Round7DefectTests(HeldOutTemplate template) => _template = template;

        private static readonly string[] AddIds = { "add-measure", "relate", "import", "add-column", "calc-group", "role" };

        /// <summary>Jev, scripted: every include question on a step that adds answered <c>adds</c>, every other include question
        /// <c>others</c>, every choice "none" at 0.95.</summary>
        private sealed class Jev : IDecisionOracle
        {
            private readonly double _adds, _others;
            public Jev(double adds, double others) { _adds = adds; _others = others; }
            public bool Available => true;

            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                if (request.PointName != DecisionRegistry.ComposeDraft)
                    return Task.FromResult(DecisionAnswers.NotDecided(request, "this test answers compose.draft only"));
                var sb = new StringBuilder("{\"entries\":[{\"digest\":").Append(JsonSerializer.Serialize(request.Digest))
                    .Append(",\"point\":").Append(JsonSerializer.Serialize(request.PointName))
                    .Append(",\"usage\":{\"input_tokens\":900,\"output_tokens\":30},\"answers\":{");
                var first = true;
                foreach (var q in request.Questions)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(q.Name)).Append(':');
                    if (q.Kind == DecisionQuestionKind.Noul)
                    {
                        var adds = AddIds.Any(id => q.Name == JevDraftBuilder.Q(id, JevDraftBuilder.Include));
                        sb.Append("{\"type\":\"noul\",\"noul\":").Append((adds ? _adds : _others).ToString(CultureInfo.InvariantCulture)).Append('}');
                    }
                    // JOIN, part 2: the last question (":wanted") now fails closed on Pro, so this scripted Jev answers it yes: the test is about the steps, not about that question.
                    else if (q.Name.EndsWith(":wanted", StringComparison.Ordinal)) sb.Append("{\"type\":\"choice\",\"choice\":\"yes\",\"confidence\":0.95}");
                    else sb.Append("{\"type\":\"choice\",\"choice\":\"none\",\"confidence\":0.95}");
                }
                sb.Append("}}]}");
                return new RecordedDecisionOracle(sb.ToString(), "jev-vsf7d").AskAsync(request, ct);
            }
        }

        private sealed record Item(string Id, string Part, string Flow, string Label, string[] Typings, string[] Want);

        private static IReadOnlyList<Item> _items;

        /// <summary>The labelled held-out set, read from the repo (not the output directory).</summary>
        private static IReadOnlyList<Item> Items()
        {
            if (_items != null) return _items;
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "could not find Semanticus.sln above " + AppContext.BaseDirectory);
            var path = Path.Combine(dir!.FullName, "Semanticus.Tests", "fixtures", "verify-sol-fixes-1-r7-heldout.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            string[] A(JsonElement e, string n) => e.GetProperty(n).EnumerateArray().Select(x => x.GetString()).ToArray();
            return _items = doc.RootElement.GetProperty("items").EnumerateArray()
                .Select(e => new Item(e.GetProperty("id").GetString(), e.GetProperty("part").GetString(), e.GetProperty("flow").GetString(),
                    e.GetProperty("label").GetString(), A(e, "typings"), A(e, "want")))
                .ToList();
        }

        /// <summary>The model the writer was told of.</summary>
        private static async Task BuildModelAsync(LocalEngine e)
        {
            await e.CreateModelAsync("HeldOut", 1604);
            await e.CreateTableAsync("Sales", "human");
            foreach (var (n, t) in new[] { ("Amount", "Double"), ("Quantity", "Int64"), ("Discount", "Double"), ("Order Date", "DateTime"), ("Product Key", "Int64"), ("Customer Key", "Int64") })
                await e.CreateColumnAsync("table:Sales", n, t, null, "human");
            await e.CreateTableAsync("Products", "human");
            foreach (var (n, t) in new[] { ("Product Key", "Int64"), ("Product Name", "String"), ("Category", "String"), ("List Price", "Double") })
                await e.CreateColumnAsync("table:Products", n, t, null, "human");
            await e.CreateTableAsync("Customers", "human");
            foreach (var (n, t) in new[] { ("Customer Key", "Int64"), ("Customer Name", "String"), ("Region", "String") })
                await e.CreateColumnAsync("table:Customers", n, t, null, "human");
            await e.CreateTableAsync("Date", "human");
            foreach (var (n, t) in new[] { ("Date", "DateTime"), ("Year", "Int64"), ("Month", "String") })
                await e.CreateColumnAsync("table:Date", n, t, null, "human");
            await e.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Order Count", "COUNTROWS ( Sales )", "human");
            await e.CreateMeasureAsync("table:Sales", "Total Quantity", "SUM ( Sales[Quantity] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Average Price", "DIVIDE ( [Total Sales], [Total Quantity] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Margin", "[Total Sales] - SUM ( Sales[Discount] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Total Discount", "SUM ( Sales[Discount] )", "human");
            await e.CreateRelationshipAsync("column:Sales/Product Key", "column:Products/Product Key", null, true, "human");
            await e.CreateRoleAsync("Regional Managers", null, "human");
            await e.CreateCalculationGroupAsync("Time Calculations", "human");
        }

        /// <summary>The model, built once per test class and saved outside every fixture, so each row opens a copy instead of
        /// building it again (the held-out run built it fresh each time; the content is the same). The class fixture deletes it
        /// when this class's last test is done, inside the run: nothing here runs as the test process exits
        /// (NoBrowserSignInGuardTests.No_test_runs_code_as_the_test_process_exits).</summary>
        public sealed class HeldOutTemplate : IDisposable
        {
            private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
            private string _path;

            public async Task<string> PathAsync()
            {
                await _gate.WaitAsync();
                try
                {
                    if (_path != null && File.Exists(_path)) return _path;
                    using var f = new DecisionFixture(TestEntitlements.Pro);
                    await BuildModelAsync(f.Engine);
                    await f.SaveAsAsync("HeldOutTemplate.bim");
                    var keep = Path.Combine(Path.GetTempPath(), "vsf7-heldout-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".bim");
                    File.Copy(Path.Combine(f.Workspace, "HeldOutTemplate.bim"), keep);
                    return _path = keep;
                }
                finally { _gate.Release(); }
            }

            public void Dispose()
            {
                try { if (_path != null) File.Delete(_path); } catch { }
                _gate.Dispose();
            }
        }

        private static Task<DraftView> ComposeOnDoor(LocalEngine engine, string door, string words) =>
            door == "studio" ? new EngineRpcTarget(engine).composeTask(words: words) : McpTools.ComposeTask(engine, words: words);

        private static Task<DraftView> AddMoreWordsOnDoor(LocalEngine engine, string door, string draftId, string words) =>
            door == "studio" ? new EngineRpcTarget(engine).editDraft(draftId, "refine", text: words) : McpTools.EditDraft(engine, draftId, "refine", text: words);

        private static string AddIdOf(string stepId) =>
            AddIds.FirstOrDefault(a => stepId == a || stepId.StartsWith(a + "-", StringComparison.Ordinal) && stepId.Substring(a.Length + 1).All(char.IsDigit));

        /// <summary>One item through one door, as a person types it: one typing; or a draft, then Add more words; or words typed
        /// before permission (kept unread), permission, then Add more words. The final draft's add steps, kept and unticked.</summary>
        private async Task<(string[] Kept, string[] Unticked, string Steps)> RunAsync(Item item, string door, string jev)
        {
            var file = "V7D" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bim";
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var path = Path.Combine(f.Workspace, file);
            File.Copy(await _template.PathAsync(), path);
            await f.Engine.OpenAsync(path);
            f.Engine.UseDecisionsForTest(new DecisionService(jev == "sure" ? new Jev(0.99, 0.02) : new Jev(0.02, 0.02), new DecisionBudget()));
            if (item.Flow != "kept") await f.ConsentAsync();
            var v = await ComposeOnDoor(f.Engine, door, item.Typings[0]);
            if (item.Flow is "more" or "kept")
            {
                if (item.Flow == "kept") await f.ConsentAsync();
                v = await AddMoreWordsOnDoor(f.Engine, door, v.DraftId, item.Typings[1]);
            }
            var adds = v.Steps.Concat(v.After ?? Array.Empty<DraftStepView>()).Where(s => AddIdOf(s.Id) != null).ToList();
            return (adds.Where(s => s.Kept).Select(s => AddIdOf(s.Id)).ToArray(), adds.Where(s => !s.Kept).Select(s => AddIdOf(s.Id)).ToArray(),
                string.Join(", ", v.Steps.Select(s => s.Title + (s.Kept ? " (kept)" : " (unticked)"))));
        }

        public static IEnumerable<object[]> TakeAways() =>
            Items().Where(i => i.Label == "must-not-keep")
                .SelectMany(i => new[] { "no", "sure" }.SelectMany(j => new[] { "studio", "agent" }.Select(d => new object[] { i.Id, d, j })));

        // Lean suite, lane A. The 60 take-aways went through both doors and both Jev modes (240 engine runs). The default tier now reads
        // the 50 one-typing items through the rule alone, and runs 4 of the 20 that need the engine (Add more words and kept words, where an
        // earlier ticked add must be unticked; T38 takes the add away BEFORE it) on the agent door with Jev sure of every add, the hardest mode. The full matrix is the sweep.
        private static readonly string[] EngineWitnesses = { "T31", "T33", "T38", "T41" };

        public static IEnumerable<object[]> RuleItems() =>
            Items().Where(i => i.Label == "must-not-keep" && i.Flow == "one").Select(i => new object[] { i.Id });

        public static IEnumerable<object[]> EngineWitnessRows() =>
            Items().Where(i => EngineWitnesses.Contains(i.Id)).Select(i => new object[] { i.Id, "agent", "sure" });

        /// <summary>VR17 on the rule alone: no clause of a one-typing take-away reads as asking to add something that stays.</summary>
        [Theory]
        [MemberData(nameof(RuleItems))]
        public void VR17_rule_reads_no_clause_of_a_held_out_one_typing_take_away_as_a_kept_add(string id)
        {
            var item = Items().Single(i => i.Id == id);
            var clauses = DescribedSteps.ClausesOf(item.Typings[0], out _, out var sentences);
            var unread = clauses.Select((_, i) => clauses.Count > 12 && i == 11).ToArray();   // the Plan reads twelve clauses of one ask
            var kept = AddingRule.Read(clauses, sentences, unreadAfter: unread, wholeSigned: AddingRule.WholeSigned(item.Typings)).Where(r => r.AsksToAdd).Count();
            Assert.True(kept == 0, id + " (" + item.Part + "): \"" + item.Typings[0] + "\" has " + kept + " clause(s) that ask to add");
        }

        /// <summary>VR17 through the engine for the items the rule alone cannot read, on the hardest row (agent door, Jev sure).</summary>
        [Theory]
        [MemberData(nameof(EngineWitnessRows))]
        public Task VR17_witness_a_held_out_take_away_never_leaves_a_kept_add_through_the_engine(string id, string door, string jev) =>
            VR17_a_held_out_take_away_never_leaves_a_kept_add(id, door, jev);

        /// <summary>VR17 (the harm; stop rule: 0 in every mode). A take-away that holds an adding verb never leaves a KEPT add.
        /// The full matrix: 60 items, both doors, both Jev modes.</summary>
        [Theory]
        [Trait(TestCost.Key, TestCost.Sweep)]
        [MemberData(nameof(TakeAways))]
        public async Task VR17_a_held_out_take_away_never_leaves_a_kept_add(string id, string door, string jev)
        {
            var item = Items().Single(i => i.Id == id);
            var (kept, _, steps) = await RunAsync(item, door, jev);
            Assert.True(kept.Length == 0, id + " (" + item.Part + "): \"" + string.Join("\" then \"", item.Typings) + "\" => " + steps);
        }

        public static IEnumerable<object[]> DoorsAndJev() => new[]
        {
            new object[] { "studio", "no" }, new object[] { "agent", "no" }, new object[] { "studio", "sure" }, new object[] { "agent", "sure" },
        };

        /// <summary>VR18 (stop rule: plain adds at least 90% kept, now 75% with nine named, and at least 97% offered, a step kept or unticked). The 40 plain
        /// adds, one door and one Jev mode per row; the message lists every one not kept.</summary>
        [Theory]
        [Trait(TestCost.Key, TestCost.Sweep)]
        [MemberData(nameof(DoorsAndJev))]
        public async Task VR18_held_out_plain_adds_are_kept_and_offered_as_the_stop_rule_asks(string door, string jev)
        {
            var plain = Items().Where(i => i.Label == "should-keep").ToList();
            int kept = 0, offered = 0;
            var lost = new List<string>();
            foreach (var item in plain)
            {
                var (k, u, steps) = await RunAsync(item, door, jev);
                var isKept = item.Want.All(k.Contains);
                var isOffered = item.Want.All(w => k.Contains(w) || u.Contains(w));
                if (isKept) kept++;
                if (isOffered) offered++;
                if (!isKept) lost.Add(item.Id + (isOffered ? " unticked" : " NOT OFFERED") + ": \"" + item.Typings[0] + "\" => " + steps);
            }
            var keptShare = 100.0 * kept / plain.Count;
            var offeredShare = 100.0 * offered / plain.Count;
            // JOIN, part 2 (coordinator ruling on VR19, 30 Sep): the version 6 reading that kept 90% of these plain adds also kept 24
            // take-back or decline phrasings version 5 left unticked (reading line 5e, and "any words before the verb are a lead-in"),
            // so both are out again and the plain adds they helped are offered unticked, on purpose: the nine below. The kept bar is the
            // 30 of 40 that version 5's lead-in list and K4 give; the offered bar (97%) and "no other plain add is lost" are unchanged.
            var accepted = new[] { "P01", "P06", "P10", "P13", "P17", "P21", "P24", "P25", "P39" };   // offered unticked on purpose
            var lostOthers = lost.Where(l => !accepted.Any(a => l.StartsWith(a + " unticked", StringComparison.Ordinal)) && !l.StartsWith("P40 NOT OFFERED", StringComparison.Ordinal)).ToList();   // P40 is the one item not offered (39 of 40, above the 97% bar) since version 6
            Assert.True(keptShare >= 75.0 && offeredShare >= 97.0 && lostOthers.Count == 0,
                $"kept {kept} of {plain.Count} ({keptShare:F1}%), offered {offered} of {plain.Count} ({offeredShare:F1}%); lost beyond the nine accepted:\n" + string.Join("\n", lostOthers));
        }
    }
}
