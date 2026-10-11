// verify: the verifier's failing tests for m11/sol-fixes-1 round 8 (68af2234, THE ADDING RULE version 6). Each test is LEFT FAILING
// on purpose: it passes once the defect is fixed. The model is the one the v5 held-out writer was told of; Jev is scripted (it answers
// the engine's exact request through the real recorded oracle). CI never calls real Jev.
//
// VR19 (real harm: written without the person seeing it first). From the assistant's door alone, words that take an add back are
// drafted with that add TICKED; the assistant answers the draft's questions, approves it with run_draft and writes its parts from the
// queue; the draft runs and the model gains the thing the words took back. The person never sees the draft: run_draft holds only row
// security for the person (ComposedOps.HeldForPerson). Jev's last question (version 6, C) is the only guard, and it is open when it
// fails: not answered (Jev slow, a spent budget, a fence) or answered yes, the rule's answer stands. The words here keep their add by
// the rule alone: a later clause read as an explanation (K4 "Explains": "so never mind", "this was a mistake", "no Temp Sales measure
// after all"), a lead-in that cancels ("Cancel the plan to add ..."), or a take-away said before the add with no word of the frozen list
// ("don't keep what I type next", which REAL Jev also answered yes to in the verifier's one look, and which then ran end to end).
// JOIN, part 2: the coordinator's ruling on VR19 is (a) reading line 5e and D's any-words lead-in back to version 5's list, (b) Jev's last
// question fails closed on Pro, and (d) an assistant's own run_draft counts as the person's review of a kept add and must NAME each kept add
// it ran. So the one row that stays a run ("don't keep what I type next" then a plain add, Jev sure) asserts (d) instead of "gains nothing".
// VR20 (round 8 brief item 3, as written: "a yes-to-everything run keeps exactly what the rule keeps"; version 5 item 2: "Jev never lifts
// an add above unticked"). With Jev answering yes to EVERY question (each step's include and the last question), adds the rule alone
// leaves unticked are kept: a later clause that leads with a verb ("undo that", "take it back") counts as having got a kept step (K4)
// once Jev is sure of any step for it. Two are items of the spent held-out set (T37, T46).

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
using Semanticus.Engine.Composer;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class VerifySolFixes1Round8DefectTests
    {
        private static readonly string[] AddIds = { "add-measure", "relate", "import", "add-column", "calc-group", "role" };

        /// <summary>Jev, scripted: every include on a step that adds answered <c>adds</c>, every other include <c>others</c>, every
        /// listed-argument choice "none"; the last question (":wanted") answered <c>wanted</c> at 0.95, or not answered when null.</summary>
        private sealed class Jev : IDecisionOracle
        {
            private readonly double _adds, _others;
            private readonly string _wanted;
            public Jev(double adds, double others, string wanted) { _adds = adds; _others = others; _wanted = wanted; }
            public bool Available => true;

            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                if (request.PointName != DecisionRegistry.ComposeDraft)
                    return Task.FromResult(DecisionAnswers.NotDecided(request, "this test answers compose.draft only"));
                if (_wanted == null && request.Questions.Any(q => q.Name.EndsWith(":wanted", StringComparison.Ordinal)))
                    return Task.FromResult(DecisionAnswers.NotDecided(request, "Jev did not answer"));
                var sb = new StringBuilder("{\"entries\":[{\"digest\":").Append(JsonSerializer.Serialize(request.Digest))
                    .Append(",\"point\":").Append(JsonSerializer.Serialize(request.PointName))
                    .Append(",\"usage\":{\"input_tokens\":900,\"output_tokens\":30},\"answers\":{");
                var first = true;
                foreach (var q in request.Questions)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(q.Name)).Append(':');
                    if (q.Name.EndsWith(":wanted", StringComparison.Ordinal))
                        sb.Append("{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(_wanted)).Append(",\"confidence\":0.95}");
                    else if (q.Kind == DecisionQuestionKind.Noul)
                    {
                        var adds = DraftMenu.All.Any(t => AddingRule.Adds(t.Op) && q.Name == JevDraftBuilder.Q(t.Id, JevDraftBuilder.Include));
                        sb.Append("{\"type\":\"noul\",\"noul\":").Append((adds ? _adds : _others).ToString(CultureInfo.InvariantCulture)).Append('}');
                    }
                    else sb.Append("{\"type\":\"choice\",\"choice\":\"none\",\"confidence\":0.95}");
                }
                sb.Append("}}]}");
                return new RecordedDecisionOracle(sb.ToString(), "jev-vsf8d").AskAsync(request, ct);
            }
        }

        /// <summary>The model the v5 held-out writer was told of (VerifySolFixes1Round7DefectTests).</summary>
        private static async Task<DecisionFixture> OpenAsync(IDecisionOracle jev)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
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
            await f.SaveAsAsync("VR8" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bim");
            e.UseDecisionsForTest(new DecisionService(jev, new DecisionBudget()));
            return f;
        }

        private static string AddIdOf(string stepId) =>
            AddIds.FirstOrDefault(a => stepId == a || stepId.StartsWith(a + "-", StringComparison.Ordinal) && stepId.Substring(a.Length + 1).All(char.IsDigit));

        private static string Shown(DraftView v) =>
            string.Join(", ", v.Steps.Concat(v.After ?? Array.Empty<DraftStepView>()).Select(s => s.Title + (s.Kept ? " (kept)" : " (unticked)")));

        public static IEnumerable<object[]> TakeBacks()
        {
            var rows = new (string First, string Then)[]
            {
                ("add a measure Temp Sales", "so never mind"),
                ("add a measure Temp Sales", "this was a mistake"),
                ("add a measure Temp Sales", "no Temp Sales measure after all"),
                ("Cancel the plan to add a measure Temp Sales", ""),
                ("don't keep what I type next", "add a role Temp Access"),
            };
            foreach (var (first, then) in rows)
                foreach (var mode in new[] { "last question not answered", "last question says yes" })
                    yield return new object[] { first, then, mode };
        }

        /// <summary>VR19. The assistant alone composes the person's words, answers, approves and writes the parts: the draft must not
        /// give the model the thing the words took back.</summary>
        [Theory]
        [MemberData(nameof(TakeBacks))]
        public async Task VR19_the_assistant_alone_never_runs_an_add_the_words_took_back(string first, string then, string mode)
        {
            using var f = await OpenAsync(new Jev(0.02, 0.02, mode == "last question says yes" ? "yes" : null));
            await f.ConsentAsync();
            var e = f.Engine;
            var before = (await e.ModelNamesAsync()).Objects.ToHashSet(StringComparer.Ordinal);
            var v = await McpTools.ComposeTask(e, words: first);
            if (then.Length > 0) v = await McpTools.EditDraft(e, v.DraftId, "refine", text: then);
            var drafted = Shown(v);
            // Every question a run needs, answered by the assistant with the first thing the draft offers.
            foreach (var q in v.Questions.Where(q => q.Answer == null && !q.Name.EndsWith("-expected", StringComparison.Ordinal) && !q.Name.EndsWith("-measure", StringComparison.Ordinal)))
            {
                var answer = q.Options?.FirstOrDefault()?.Ref ?? q.Values.FirstOrDefault();
                if (answer != null) v = await McpTools.EditDraft(e, v.DraftId, "answer", question: q.Name, text: answer);
            }
            var ranAnswer = await McpTools.RunDraft(e, v.DraftId);
            v = ranAnswer;
            // Every part on the assistant's queue, written.
            for (var round = 0; round < 3; round++)
            {
                v = await McpTools.GetDraft(e, v.DraftId);
                var open = v.Blanks.Where(b => b.RequestId != null && b.State != "passed").ToList();
                if (open.Count == 0) break;
                foreach (var b in open)
                {
                    var claim = await McpTools.ClaimRequest(e, b.RequestId);
                    if (!claim.Ok) continue;
                    var name = (b.Name ?? "").ToLowerInvariant();
                    await McpTools.CompleteRequest(e, b.RequestId, claim.Request.ClaimToken,
                        name.Contains("expression", StringComparison.Ordinal) ? "SUM ( Sales[Amount] )" : "Temp Access");
                }
            }
            v = await McpTools.GetDraft(e, v.DraftId);
            var gained = (await e.ModelNamesAsync()).Objects.Where(x => !before.Contains(x)).ToList();
            // JOIN, part 2 (coordinator ruling on this defect, item 2d): an assistant's own run_draft counts as the person's review of a
            // kept add, so the ONE row where the rule keeps the add by its words alone and Jev is sure of it ("don't keep what I type next",
            // then a plain add, last question says yes) is allowed to run. What the ruling asks instead is that run_draft's answer NAMES
            // the kept add it runs, so the assistant that ran it and the person who reads its answer both see it.
            if (first == "don't keep what I type next" && mode == "last question says yes")
            {
                Assert.NotEmpty(gained);   // not vacuous: the ruling's case really runs
                Assert.Contains("Add a role", ranAnswer.Sentence ?? "", StringComparison.Ordinal);
                Assert.Contains("add a role Temp Access", ranAnswer.Sentence ?? "", StringComparison.Ordinal);
                return;
            }
            Assert.True(gained.Count == 0,
                "\"" + first + (then.Length > 0 ? "\" then \"" + then : "") + "\" (" + mode + "): drafted " + drafted + "; after the assistant's run_draft the draft is "
                + v.State + " (" + v.Summary + ") and the model gained " + string.Join(", ", gained));
        }

        public static IEnumerable<object[]> Lifts() => new[]
        {
            new object[] { "T37", "more", "add a column Line Total to Sales = Sales[Amount] * Sales[Quantity]", "take back the Line Total column" },
            new object[] { "T46", "kept", "relate Sales[Order Date] to Date[Date]", "undo the relationship between Sales Order Date and Date" },
            new object[] { "undo", "more", "add a role called Interns", "undo that" },
            new object[] { "take it back", "more", "add a measure Avg Order Value = DIVIDE([Total Sales],[Order Count]).", "take it back" },
            new object[] { "use the old", "one", "add a measure Temp Sales. Use the old Total Sales instead.", "" },
        }.SelectMany(r => new[] { "studio", "agent" }.Select(d => r.Append(d).ToArray()));

        /// <summary>VR20. Jev answering yes to every question keeps exactly what the rule alone keeps: nothing the words leave
        /// unticked is ticked.</summary>
        [Theory]
        [MemberData(nameof(Lifts))]
        public async Task VR20_jev_answering_yes_to_everything_keeps_exactly_what_the_rule_keeps(string id, string flow, string first, string then, string door)
        {
            using var f = await OpenAsync(new Jev(0.99, 0.99, "yes"));
            var e = f.Engine;
            var (tables, objects) = await e.ModelNamesAsync();
            var typed = then.Length > 0 ? new[] { first, then } : new[] { first };
            var rule = DescribedSteps.RuleOfWords(string.Join("\n", typed), tables, objects);
            Assert.DoesNotContain(rule, r => r.Tier == AddingRule.Tier.Kept);   // the rule alone keeps no add here
            if (flow != "kept") await f.ConsentAsync();
            var v = door == "studio" ? await new EngineRpcTarget(e).composeTask(words: first) : await McpTools.ComposeTask(e, words: first);
            if (flow == "kept") await f.ConsentAsync();
            if (then.Length > 0)
                v = door == "studio" ? await new EngineRpcTarget(e).editDraft(v.DraftId, "refine", text: then) : await McpTools.EditDraft(e, v.DraftId, "refine", text: then);
            var kept = v.Steps.Concat(v.After ?? Array.Empty<DraftStepView>()).Where(s => s.Kept && AddIdOf(s.Id) != null).ToList();
            Assert.True(kept.Count == 0, id + " (" + door + "): \"" + string.Join("\" then \"", typed) + "\" => " + Shown(v));
        }
    }
}
