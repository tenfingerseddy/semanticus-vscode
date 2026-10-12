// m11/sol-fixes-1 round 8: THE ADDING RULE version 6 (the coordinator, 30 Sep 00:10, after the version 5 held-out run failed both
// ways; /tmp/semanticus-joint-review/dispatch/rule-adding-v2.md, "VERSION 6", A to E). What this file proves:
//  1. THE TRUTH TABLE, extended. The version 5 grid is kept (AddingRuleV5Tests) and this one adds the version 6 factors: the
//     lead-ins ("Quick one - add", "Per ticket BI-2231 add", "how do I add", "don't add"), the new verbs and wants ("pull in",
//     "bring in", "we need a", "I'd love a", "new X"), a bare named object the model lacks and one it has, the calc group kind,
//     trailing explanations, and typings before and after the add with a whole-text sign. Each row's expected tier is computed
//     HERE from the row's factors and never from AddingRule. 100% on the rule alone and on a door sample, on both doors, with
//     Jev saying no, Jev sure of every add, Jev demoting (the last question says no), Jev yes to everything, and Jev silent.
//  2. THE JEV QUESTION ONLY DEMOTES (C). Jev answering yes to everything keeps exactly what the rule keeps; Jev answering no or
//     unsure to the last question turns a kept add unticked; Jev not answering does too (it fails closed, join part 2); one call per kept add,
//     with the whole text in view.
//  3. THE WHOLE TEXT (A, B). A later typing that takes a ticked add back unticks it, with the note; a sign anywhere in any typing
//     demotes a kept add; a step the person ticked by hand stays ticked.
// The version 5 held-out set is spent: VR17 and VR18 (VerifySolFixes1Round7DefectTests) are regression evidence and are not tuned
// to here. Nothing in this file is a held-out set.

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
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    public sealed class AddingRuleV6Tests
    {
        private readonly ITestOutputHelper _out;
        public AddingRuleV6Tests(ITestOutputHelper output) { _out = output; }

        /// <summary>The model DecisionFixture builds: the Sales table with an Amount column, and the measures Order Count and
        /// Total Sales. The rule alone is told the same names.</summary>
        private static readonly string[] Tables = { "Sales" };
        private static readonly string[] Objects = { "Sales", "Amount", "Order Count", "Total Sales" };

        // ---- the factors ---------------------------------------------------------------------------------------------

        /// <summary>What a lead-in does to an adding verb after it: asks (the version 5 list of lead-ins, "can you"), makes it a
        /// question about ability (unticked, offered), is not on the list (unticked, offered), or negates it (not drafted).</summary>
        private static readonly (string Text, string Effect)[] LeadIns =
        {
            ("", "asks"), ("please ", "asks"), ("can you ", "asks"),
            // JOIN, part 2 (coordinator ruling on VR19): the version 6 rule that read ANY words before the adding verb as a lead-in
            // kept 24 take-back or decline phrasings version 5 left unticked, so it is back to version 5's list of lead-ins. Words
            // that are not on that list ("Quick one - add") still offer the add, unticked ("unlisted"); they no longer tick it.
            ("Quick one - ", "unlisted"), ("Per ticket BI-2231 ", "unlisted"),
            ("Following up on the review - ", "unlisted"), ("Before I forget - ", "unlisted"), ("Last thing before I ship - ", "unlisted"),
            ("how do I ", "questions"), ("don't ", "negates"), ("no need to ", "negates"),
        };

        /// <summary>An adding verb (or a verb that adds nothing). BareOk: it reads a BARE object ("add measure X"); "make" and
        /// "build" read only an object that starts a, an, another, new, one, some or a number, so "make Total Sales hidden"
        /// stays a change of what is there.</summary>
        private static readonly (string Text, bool Adding, bool BareOk)[] Verbs =
            { ("add", true, true), ("create", true, true), ("set up", true, true), ("build", true, false), ("make", true, false), ("hide", false, false), ("describe", false, false) };

        private sealed record Kind(string Word, string Plural, string Id, string Op, string Lack, string Has);

        private static readonly Kind[] Kinds =
        {
            new Kind("measure", "measures", "add-measure", "create_measure", "Rebate", "Total Sales"),
            new Kind("role", "roles", "role", "create_role", "Managers", null),
            new Kind("column", "columns", "add-column", "create_column", "Region", "Amount"),
            new Kind("calculation group", "calculation groups", "calc-group", "create_calculation_group", "Time", null),
            new Kind("calc group", "calc groups", "calc-group", "create_calculation_group", "Scaling", null),
        };

        /// <summary>The object's form. marked: starts a, an, another or a number. bare and bareLast: no determiner, the kind
        /// named, a name the model LACKS ("measure Rebate", "Rebate measure"). bareHas and bareHasLast: the same with a name
        /// the model HAS ("measure Total Sales"). definite: the, that. nokind: names no kind.</summary>
        private static readonly (string Text, string Shape)[] Forms =
        {
            ("a {K}", "marked"), ("a new {K}", "marked"), ("another {K}", "marked"), ("two {P}", "marked"),
            ("{K} {LACK}", "bare"), ("{LACK} {K}", "bareLast"), ("{K} {HAS}", "bareHas"), ("{HAS} {K}", "bareHasLast"),
            ("the {LACK} {K}", "definite"), ("that {K}", "definite"), ("the same", "nokind"), ("Total Sales", "nokind"),
        };

        /// <summary>What follows the thing in its own clause (or after a comma); true when the add stays kept. Sales is the
        /// model's table, Marketing is not. The last four are trailing explanations (D, K4).</summary>
        private static readonly (string Text, bool Fine)[] Tails =
        {
            ("", true), (" for margin", true), (" for the finance team", true), (" to the Sales table", true),
            (" to the Marketing table", false), (" to them", false), (" to the old ones", false), (" to that list", false),
            (" that we might delete", false), (" with fewer filters", false),
            // JOIN, part 2 (coordinator ruling on VR19): reading line 5e ("explains" openers: which, that's why, since, because, no filters)
            // is back out, because it kept 13 take-backs ("so never mind", "this was a mistake", "no Temp Sales measure after all") that
            // version 5 left unticked. A trailing explanation is an unread clause again (K4), so the add is offered unticked.
            (" because the team asked for it", true), (", which we will use for reports", false),   // "because" stays inside the adding clause
            (", that's why the reports are blank", false), (", since those reps shouldn't see it", false),
        };

        /// <summary>What comes after the ask in its own typing; true when every later clause got a step, is a pleasantry,
        /// explains, or is only a noun phrase of the same sentence.</summary>
        private static readonly (string Text, bool Fine)[] Laters =
        {
            ("", true), (". No.", false), (". Then delete it.", false), (", then hide Total Cost", true), (". The old ones can go.", false),
            (", thanks", true), (". Hide Total Cost.", true), (". Thanks.", true), (". Never mind.", false), (". Actually, forget it.", false),
            (". No filters, they see everything.", false),   // 5e is out (see Tails)
        };

        /// <summary>Typings around the add's own (Add more words): an earlier one, or a later one. Fine when the add stays kept.
        /// A sign anywhere in any typing, or a later typing that takes it back with no sign, does not.</summary>
        private static readonly (string Name, string Earlier, string LaterTyping, bool Fine)[] Flows =
        {
            ("single", null, null, true),
            ("earlier harmless", "Sales looks fine.", null, true),
            ("earlier sign", "Anything in my next message goes on the list to delete.", null, false),
            ("earlier delete", "Please delete the old measures.", null, false),
            ("later thanks", null, "Thanks.", true),
            ("later hide", null, "Hide Total Cost.", true),
            ("later sign", null, "Actually delete it, I don't want it.", false),
            ("later never mind", null, "Never mind that one.", false),
            ("later explains", null, "It is for the finance team.", false),   // 5e is out: an explanation typed later is an unread clause
        };

        private sealed record Row(string Id, string[] Typings, string Clause, string Expect, Kind Kind, bool Named, string Flow, string Lead, string Shape);

        private static string Fill(string form, Kind k) =>
            form.Replace("{K}", k.Word, StringComparison.Ordinal).Replace("{P}", k.Plural, StringComparison.Ordinal)
                .Replace("{LACK}", k.Lack, StringComparison.Ordinal).Replace("{HAS}", k.Has ?? "", StringComparison.Ordinal);

        /// <summary>The clause as the note quotes it: the first clause of the words, less a leading "please".</summary>
        private static string ClauseOf(string text)
        {
            var first = text.Split(',')[0].Trim();
            return first.StartsWith("please ", StringComparison.Ordinal) ? first.Substring("please ".Length) : first;
        }

        /// <summary>The expected tier, computed from the factors alone.</summary>
        private static string ExpectOf(bool verbAdding, bool bareOk, string leadEffect, string shape, bool tailFine, bool laterFine, bool flowFine)
        {
            if (!verbAdding || leadEffect == "negates") return "notdrafted";
            if (leadEffect is "questions" or "unlisted") return "unticked";
            var newEnough = shape == "marked" || shape is "bare" or "bareLast" && bareOk;
            var named = shape != "nokind";
            return newEnough && named && tailFine && laterFine && flowFine ? "kept" : "unticked";
        }

        private static IEnumerable<Row> Block(IEnumerable<(string Text, string Effect)> leads, IEnumerable<(string Text, bool Adding, bool BareOk)> verbs,
            IEnumerable<(string Text, string Shape)> forms, IEnumerable<(string Text, bool Fine)> tails, IEnumerable<(string Text, bool Fine)> laters,
            IEnumerable<(string Name, string Earlier, string LaterTyping, bool Fine)> flows, string tag)
        {
            var n = 0;
            foreach (var (lead, effect) in leads)
            foreach (var (verb, adding, bareOk) in verbs)
            foreach (var k in Kinds)
            foreach (var (form, shape) in forms)
            {
                // "the same" and "Total Sales" name no kind; a "has" form needs a kind the model has a thing of.
                if (shape == "nokind" && k != Kinds[0]) continue;
                if (shape is "bareHas" or "bareHasLast" && k.Has == null) continue;
                foreach (var (tail, tailFine) in tails)
                foreach (var (later, laterFine) in laters)
                foreach (var flow in flows)
                {
                    var clauseText = lead + verb + " " + Fill(form, k) + tail;
                    var main = clauseText + later;
                    var typings = flow.Earlier != null ? new[] { flow.Earlier, main } : flow.LaterTyping != null ? new[] { main, flow.LaterTyping } : new[] { main };
                    yield return new Row(tag + (++n).ToString(CultureInfo.InvariantCulture), typings, ClauseOf(clauseText),
                        ExpectOf(adding, bareOk, effect, shape, tailFine, laterFine, flow.Fine), k, shape != "nokind", flow.Name, effect, shape);
                }
            }
        }

        private static readonly (string Name, string Earlier, string LaterTyping, bool Fine)[] Single = { Flows[0] };

        /// <summary>The kind an import verb makes: a table, by its own step.</summary>
        private static readonly Kind ImportKind = new Kind("table", "tables", "import", "create_import_table", "Budget", null);

        /// <summary>The wants and the import verbs, which the lead-in and verb grid does not carry.</summary>
        private static IEnumerable<Row> Wants()
        {
            var n = 0;
            // a bare want of a new thing: "new X", "I'd love a X", "we need a X"; and the forms that are not one
            var wants = new (string Text, bool Adding)[]
            {
                ("we need a {K}", true), ("I'd love a {K}", true), ("we want another {K}", true), ("new {K}", true),
                ("we need a new {K}", true), ("a new {K}", false), ("we need {P}", false), ("the new {K}", false),
            };
            // a tail holding a helping verb ("that we might delete") makes a bare want a clause with a verb of its own, which reads no
            // add at all, so it is left out of this block
            foreach (var (form, adding) in wants)
            foreach (var k in Kinds)
            foreach (var (tail, tailFine) in Tails.Where(t => !t.Text.Contains("might", StringComparison.Ordinal)))
            foreach (var (later, laterFine) in Laters)
            {
                var clause = Fill(form, k) + tail;
                yield return new Row("want" + (++n).ToString(CultureInfo.InvariantCulture), new[] { clause + later }, ClauseOf(clause),
                    adding && tailFine && laterFine ? "kept" : adding ? "unticked" : "notdrafted", k, true, "single", "", "want");
            }
            // import verbs, with their own objects: a table (kept), a role (not a table: unticked)
            var imports = new (string Text, bool Kept)[]
            {
                ("the Budget table", true), ("a Budget table", true), ("a new table for budgets", true), ("the Managers role", false),
            };
            foreach (var verb in new[] { "import", "pull in", "bring in" })
            foreach (var (lead, effect) in LeadIns)
            foreach (var (obj, isTable) in imports)
            foreach (var (tail, tailFine) in new (string, bool)[] { ("", true), (" from Budget.xlsx", true), (" to them", false), (" to that list", false) })
            foreach (var (later, laterFine) in new (string, bool)[] { ("", true), (". No.", false), (". Then delete it.", false), (", thanks", true) })
            {
                var clause = lead + verb + " " + obj + tail;
                yield return new Row("imp" + (++n).ToString(CultureInfo.InvariantCulture), new[] { clause + later }, ClauseOf(clause),
                    effect == "negates" ? "notdrafted" : effect is "questions" or "unlisted" ? "unticked" : isTable && tailFine && laterFine ? "kept" : "unticked",
                    ImportKind, isTable, "single", effect, "import");
            }
        }

        /// <summary>The grid: every row's expected tier from its factors alone. Blocks, so the cross product of all the factors
        /// (millions of rows) is covered where each factor meets the others that can change its answer.</summary>
        private static IEnumerable<Row> Grid()
        {
            // 1. lead-ins x verbs x kinds x forms, with the tails and laters that decide the tier by themselves
            foreach (var r in Block(LeadIns, Verbs, Forms, Tails.Where(t => t.Text is "" or " for margin" or " to them" or ", which we will use for reports"),
                Laters.Where(l => l.Text is "" or ". No." or ", thanks"), Single, "a")) yield return r;
            // 2. the plain verbs x every tail x every later
            foreach (var r in Block(LeadIns.Where(l => l.Text is "" or "please "), Verbs.Where(v => v.Text is "add" or "create" or "set up"), Forms, Tails, Laters, Single, "b")) yield return r;
            // 3. typings before and after (Add more words), with the version 5 tails and laters that stay meaningful
            foreach (var r in Block(LeadIns.Where(l => l.Text is "" or "Quick one - "), Verbs.Where(v => v.Text is "add" or "hide"), Forms,
                Tails.Where(t => t.Text is "" or " to them" or ", which we will use for reports"), Laters.Where(l => l.Text is "" or ". No." or ", thanks"), Flows.Skip(1), "c")) yield return r;
            // 4. wants and the import verbs
            foreach (var r in Wants()) yield return r;
        }

        private static string TierOf(AddingRule.Reading[] rule) =>
            rule.Any(r => r.Tier == AddingRule.Tier.Kept) ? "kept" : rule.Any(r => r.Tier == AddingRule.Tier.Unticked) ? "unticked" : "notdrafted";

        /// <summary>The truth table on the rule alone: every row reads as the grid says, in both directions. The typings are
        /// read as one text, joined by a line break, as the engine reads them.</summary>
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public void The_truth_table_holds_on_the_rule_alone()
        {
            var rows = Grid().ToList();
            var wrong = new List<string>();
            var counts = new Dictionary<string, int>();
            foreach (var row in rows)
            {
                var got = TierOf(DescribedSteps.RuleOfWords(string.Join("\n", row.Typings), Tables, Objects));
                counts[row.Expect] = counts.GetValueOrDefault(row.Expect) + 1;
                if (got != row.Expect) wrong.Add(row.Expect + " expected, " + got + " read: " + string.Join("  //  ", row.Typings));
            }
            _out.WriteLine("grid rows " + rows.Count + ": " + string.Join(", ", counts.Select(x => x.Key + " " + x.Value)) + "; wrong " + wrong.Count);
            Assert.True(wrong.Count == 0, wrong.Count + " of " + rows.Count + " rows read wrong:\n" + string.Join("\n", wrong.Take(60)));
            Assert.All(new[] { "kept", "unticked", "notdrafted" }, t => Assert.True(counts.GetValueOrDefault(t) > 1000, t));
        }

        /// <summary>A kept add is of the kind the words name: the rule says which step, and no other.</summary>
        [Fact]
        public void A_kept_add_is_of_the_kind_the_words_name()
        {
            foreach (var row in Grid().Where(r => r.Expect == "kept"))
            {
                var rule = DescribedSteps.RuleOfWords(string.Join("\n", row.Typings), Tables, Objects);
                var kept = rule.Single(r => r.Tier == AddingRule.Tier.Kept);
                Assert.True(kept.Kind == row.Kind.Op, string.Join(" // ", row.Typings) + " => " + kept.Kind);
            }
        }

        // ---- Jev, scripted -----------------------------------------------------------------------------------------------

        /// <summary>The requests Jev was asked, so a test can count the last question's calls and read what was in view.</summary>
        private sealed class Asked
        {
            public readonly List<(string Question, string DraftWords)> Wanted = new List<(string, string)>();
        }

        /// <summary>Jev, scripted. <c>adds</c> answers every include question on a step that adds, <c>others</c> every other
        /// include question, every choice about a listed argument is "none". <c>wanted</c> answers the last question (a choice
        /// of yes, no or unsure): "yes", "no" or "unsure", or null for not answering at all (Jev slow or off).</summary>
        private sealed class Jev : IDecisionOracle
        {
            private readonly double _adds, _others;
            private readonly string _wanted;
            private readonly Asked _asked;
            private static readonly object Gate = new object();
            public Jev(double adds, double others, string wanted, Asked asked = null) { _adds = adds; _others = others; _wanted = wanted; _asked = asked; }
            public bool Available => true;

            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                if (request.PointName != DecisionRegistry.ComposeDraft)
                    return Task.FromResult(DecisionAnswers.NotDecided(request, "this test answers compose.draft only"));
                var wanted = request.Questions.Where(q => q.Name.EndsWith(":wanted", StringComparison.Ordinal)).ToList();
                if (wanted.Count > 0)
                {
                    lock (Gate)
                        foreach (var q in wanted) _asked?.Wanted.Add((q.Name, request.Snapshot.TryGetValue("draft_words", out var w) ? w as string : null));
                    if (_wanted == null) return Task.FromResult(DecisionAnswers.NotDecided(request, "Jev did not answer"));
                }
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
                return new RecordedDecisionOracle(sb.ToString(), "jev-sf1r8").AskAsync(request, ct);
            }
        }

        /// <summary>The five ways Jev can stand: saying no to every step, sure of every add, sure of every add and saying no to
        /// the last question (demoting), saying yes to everything, and not answering the last question at all.</summary>
        private static IDecisionOracle JevFor(string mode, Asked asked = null) => mode switch
        {
            "no" => new Jev(0.02, 0.02, "yes", asked),
            "sure" => new Jev(0.99, 0.02, "yes", asked),
            "demote" => new Jev(0.99, 0.02, "no", asked),
            "unsure" => new Jev(0.99, 0.02, "unsure", asked),
            "yes" => new Jev(0.02, 0.02, "yes", asked),
            "silent" => new Jev(0.99, 0.02, null, asked),
            _ => throw new ArgumentException(mode),
        };

        private static async Task<DecisionFixture> ProAsync(string name, IDecisionOracle jev)
        {
            var file = name + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bim";
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync(file);
            await f.SaveAsAsync(file);
            await f.ConsentAsync();
            f.Engine.UseDecisionsForTest(new DecisionService(jev, new DecisionBudget()));
            return f;
        }

        private static Task<DraftView> ComposeOnDoor(LocalEngine engine, string door, string words) =>
            door == "studio" ? new EngineRpcTarget(engine).composeTask(words: words) : McpTools.ComposeTask(engine, words: words);

        private static Task<DraftView> AddMoreWordsOnDoor(LocalEngine engine, string door, string draftId, string words) =>
            door == "studio" ? new EngineRpcTarget(engine).editDraft(draftId, "refine", text: words) : McpTools.EditDraft(engine, draftId, "refine", text: words);

        private static Task<DraftView> KeepOnDoor(LocalEngine engine, string door, string draftId, string step) =>
            door == "studio" ? new EngineRpcTarget(engine).editDraft(draftId, "keep", step: step) : McpTools.EditDraft(engine, draftId, "keep", step: step);

        private static readonly string[] AddIds = { "add-measure", "relate", "import", "add-column", "calc-group", "role" };

        private static bool IsAdd(DraftStepView s) =>
            AddIds.Any(a => s.Id == a || s.Id.StartsWith(a + "-", StringComparison.Ordinal) && s.Id.Substring(a.Length + 1).All(char.IsDigit));

        private static DraftStepView[] AddSteps(DraftView v) => v.Steps.Concat(v.After ?? Array.Empty<DraftStepView>()).Where(IsAdd).ToArray();

        private static DraftStepView[] KeptAdds(DraftView v) => AddSteps(v).Where(s => s.Kept).ToArray();

        /// <summary>The tier a draft shows: a ticked add is kept; an add offered dropped, or a "Not ticked" note, is unticked;
        /// no add and no such note is not drafted.</summary>
        private static string DoorTier(DraftView v) =>
            KeptAdds(v).Length > 0 ? "kept"
            : AddSteps(v).Length > 0 || v.Notes.Any(n => n.StartsWith("Not ticked", StringComparison.Ordinal)) ? "unticked" : "notdrafted";

        /// <summary>A row typed as a person types it: the first typing composes, each later one is Add more words.</summary>
        private static async Task<DraftView> TypeAsync(LocalEngine engine, string door, string[] typings)
        {
            var v = await ComposeOnDoor(engine, door, typings[0]);
            for (var i = 1; i < typings.Length; i++) v = await AddMoreWordsOnDoor(engine, door, v.DraftId, typings[i]);
            return v;
        }

        /// <summary>A sample of the grid: every 1,500th row, plus the first row of each tier with each flow, each lead-in effect
        /// and each object shape, so no factor is missed for a tier.</summary>
        private static IEnumerable<Row> Sample()
        {
            var all = Grid().ToList();
            var seen = new HashSet<string>();
            for (var i = 0; i < all.Count; i++)
            {
                var r = all[i];
                var take = i % 1500 == 0;
                take |= seen.Add("flow|" + r.Expect + "|" + r.Flow);
                take |= seen.Add("lead|" + r.Expect + "|" + r.Lead);
                take |= seen.Add("shape|" + r.Expect + "|" + r.Shape + "|" + (r.Kind?.Id ?? ""));
                if (take) yield return r;
            }
        }

        public static IEnumerable<object[]> DoorsAndJev() =>
            new[] { "studio", "agent" }.SelectMany(d => new[] { "no", "sure", "demote", "yes", "silent" }.Select(j => new object[] { d, j }));

        /// <summary>The truth table through both doors, Jev in each of five ways. What Jev's last question adds is only a
        /// demotion: with it saying no or unsure, or not answering (it fails closed), a kept row is unticked, and in every other mode
        /// the row shows the tier the rule reads.</summary>
        [Theory]
        [MemberData(nameof(DoorsAndJev))]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task The_truth_table_holds_through_the_door_with_jev_in_each_way(string door, string jev)
        {
            using var f = await ProAsync("V6Grid" + door + jev, JevFor(jev));
            var rows = Sample().ToList();
            var wrong = new List<string>();
            foreach (var row in rows)
            {
                var expect = jev is "demote" or "silent" && row.Expect == "kept" ? "unticked" : row.Expect;   // silent: the last question fails closed (join part 2)
                var v = await TypeAsync(f.Engine, door, row.Typings);
                var got = DoorTier(v);
                var shown = string.Join("  //  ", row.Typings);
                if (got != expect) { wrong.Add(expect + " expected, " + got + " shown: " + shown); continue; }
                var adds = AddSteps(v);
                if (expect == "kept")
                {
                    if (KeptAdds(v).Length != 1 || KeptAdds(v).Count(s => s.Id.StartsWith(row.Kind.Id, StringComparison.Ordinal)) != 1)
                        wrong.Add("kept but not as the one step of its kind: " + shown + " => " + string.Join(", ", KeptAdds(v).Select(s => s.Title)));
                }
                else if (expect == "unticked")
                {
                    if (adds.Any(s => s.Kept)) wrong.Add("a ticked add under words the rule offers unticked: " + shown);
                    if (!v.Notes.Any(n => n.StartsWith("Not ticked", StringComparison.Ordinal) && n.Contains("\"" + row.Clause + "\"", StringComparison.Ordinal)))
                        wrong.Add("no note with the person's words (" + row.Clause + ") for: " + shown + " notes: " + string.Join(" | ", v.Notes));
                    if (row.Named && !adds.Any(s => !s.Kept && s.Id.StartsWith(row.Kind.Id, StringComparison.Ordinal)))
                        wrong.Add("no unticked step of the kind named for: " + shown);
                }
                else if (adds.Length > 0 || v.Notes.Any(n => n.StartsWith("Not ticked", StringComparison.Ordinal)))
                    wrong.Add("an add or a Not ticked note where no adding verb asks: " + shown);
            }
            _out.WriteLine(door + " / Jev " + jev + ": " + rows.Count + " rows, wrong " + wrong.Count);
            Assert.True(wrong.Count == 0, wrong.Count + " of " + rows.Count + " rows wrong:\n" + string.Join("\n", wrong.Take(30)));
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        public static IEnumerable<object[]> The_truth_table_holds_through_the_door_with_jev_in_each_way_SampleRows() => DoorsAndJev().Take(1);

        [Theory]
        [MemberData(nameof(The_truth_table_holds_through_the_door_with_jev_in_each_way_SampleRows))]
        public Task The_truth_table_holds_through_the_door_with_jev_in_each_way_sample(string door, string jev) => The_truth_table_holds_through_the_door_with_jev_in_each_way(door, jev);

        // ---- C: Jev's last question only demotes -------------------------------------------------------------------------

        /// <summary>One call per kept add, with the whole text in view: a single kept add is asked once with its typing; an add
        /// kept by an earlier typing is asked again with BOTH typings in view; an add the rule does not keep is never asked.</summary>
        [Theory]
        [InlineData("studio")]
        [InlineData("agent")]
        public async Task Jev_is_asked_once_per_kept_add_with_the_whole_text_in_view(string door)
        {
            var asked = new Asked();
            using var f = await ProAsync("V6Asked" + door, JevFor("yes", asked));
            var v = await ComposeOnDoor(f.Engine, door, "add a role for managers and add a measure for margin");
            Assert.Equal(2, KeptAdds(v).Length);
            Assert.Equal(2, asked.Wanted.Count);
            Assert.All(asked.Wanted, x => Assert.Equal("add a role for managers and add a measure for margin", x.DraftWords));
            Assert.Contains(asked.Wanted, x => x.Question == "step:role:wanted");
            Assert.Contains(asked.Wanted, x => x.Question == "step:add-measure:wanted");

            asked.Wanted.Clear();
            v = await AddMoreWordsOnDoor(f.Engine, door, v.DraftId, "and hide the Amount column");
            Assert.Equal(2, KeptAdds(v).Length);
            // the two adds are still kept by the rule, so each is asked again, now with both typings in view
            Assert.Equal(2, asked.Wanted.Count);
            Assert.All(asked.Wanted, x => Assert.Contains("hide the Amount column", x.DraftWords));
            Assert.All(asked.Wanted, x => Assert.Contains("add a role for managers", x.DraftWords));

            asked.Wanted.Clear();
            v = await ComposeOnDoor(f.Engine, door, "add a role for managers to that list");   // unticked by the rule: not asked
            Assert.Empty(KeptAdds(v));
            Assert.Empty(asked.Wanted);
        }

        /// <summary>Jev saying no or unsure to the last question turns the kept add unticked, with a note in the person's words and
        /// a true line; Jev silent does the same (the question fails closed, with its own line); Jev saying yes keeps it. Never the other way round.</summary>
        [Theory]
        [InlineData("studio", "demote", "unticked")]
        [InlineData("agent", "demote", "unticked")]
        [InlineData("studio", "unsure", "unticked")]
        [InlineData("agent", "unsure", "unticked")]
        [InlineData("studio", "yes", "kept")]
        [InlineData("agent", "yes", "kept")]
        // JOIN, part 2 (coordinator ruling on VR19): the last question FAILS CLOSED on Pro. Jev not answering (slow, off, refused) used to
        // leave the rule's answer ("kept"); no sure yes now means unticked, with its own true line (NotCheckedReason).
        [InlineData("studio", "silent", "unticked")]
        [InlineData("agent", "silent", "unticked")]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Jev_can_only_demote_and_says_so_in_the_note(string door, string jev, string tier)
        {
            using var f = await ProAsync("V6Demote" + door + jev, JevFor(jev));
            var v = await ComposeOnDoor(f.Engine, door, "add a role for managers");
            Assert.Equal(tier, DoorTier(v));
            if (tier == "unticked")
            {
                Assert.Contains(AddSteps(v), s => !s.Kept && s.Id.StartsWith("role", StringComparison.Ordinal));
                Assert.Contains(v.Notes, n => n.StartsWith("Not ticked", StringComparison.Ordinal) && n.Contains("\"add a role for managers\"", StringComparison.Ordinal)
                                              && n.Contains(jev == "silent" ? AddingRule.NotCheckedReason : AddingRule.NotSureWantedReason, StringComparison.Ordinal));
            }
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData("studio", "demote", "unticked")]
        public Task Jev_can_only_demote_and_says_so_in_the_note_sample(string door, string jev, string tier) => Jev_can_only_demote_and_says_so_in_the_note(door, jev, tier);

        /// <summary>Jev saying yes to the last question, and sure of every include, lifts nothing: what the rule turns down stays
        /// unticked or not drafted, on both doors.</summary>
        [Theory]
        [InlineData("studio")]
        [InlineData("agent")]
        public async Task Jev_yes_lifts_nothing_the_rule_turns_down(string door)
        {
            using var f = await ProAsync("V6Lift" + door, new Jev(0.99, 0.99, "yes"));
            foreach (var (words, tier) in new[]
            {
                ("add a role for managers to that list", "unticked"), ("add a measure for margin. Then delete it.", "unticked"),
                ("add measure Total Sales", "unticked"), ("hide the Amount column", "notdrafted"), ("don't add a role", "notdrafted"),
            })
            {
                var v = await ComposeOnDoor(f.Engine, door, words);
                Assert.True(DoorTier(v) == tier, words + " => " + DoorTier(v));
            }
        }

        // ---- A and B: the whole text, every time ---------------------------------------------------------------------------

        /// <summary>A: a later typing that takes a ticked add back, or puts it on a list to delete, turns it unticked with the note,
        /// whatever words it uses. The first typing keeps the add; nothing already ticked is exempt.</summary>
        [Theory]
        [InlineData("studio", "actually delete Avg Order Value, don't want it")]
        [InlineData("agent", "actually delete Avg Order Value, don't want it")]
        [InlineData("studio", "take back the Avg Order Value measure")]
        [InlineData("agent", "take back the Avg Order Value measure")]
        [InlineData("studio", "Avg Order Value goes on the list of measures to delete")]
        [InlineData("agent", "undo the Avg Order Value measure")]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task A_later_typing_that_takes_a_ticked_add_back_unticks_it(string door, string later)
        {
            using var f = await ProAsync("V6Back" + door, JevFor("no"));
            var v = await ComposeOnDoor(f.Engine, door, "add a measure Avg Order Value = DIVIDE([Total Sales],[Order Count])");
            Assert.Equal("kept", DoorTier(v));
            v = await AddMoreWordsOnDoor(f.Engine, door, v.DraftId, later);
            Assert.Empty(KeptAdds(v));
            Assert.Contains(AddSteps(v), s => !s.Kept && s.Id.StartsWith("add-measure", StringComparison.Ordinal));
            Assert.Contains(v.Notes, n => n.StartsWith("Not ticked", StringComparison.Ordinal) && n.Contains("add a measure Avg Order Value", StringComparison.Ordinal));
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData("studio", "actually delete Avg Order Value, don't want it")]
        public Task A_later_typing_that_takes_a_ticked_add_back_unticks_it_sample(string door, string later) => A_later_typing_that_takes_a_ticked_add_back_unticks_it(door, later);

        /// <summary>B: a take-away said BEFORE the add, pointing at it, demotes it; the words say the same as the version 5 held-out
        /// items T38 and T43 did, spelt here as the rule's own factors (a sign of the frozen list anywhere in any typing).</summary>
        [Theory]
        [InlineData("studio", "anything in my next message goes on the list to delete", "add a role Temp Access")]
        [InlineData("agent", "anything in my next message goes on the list to delete", "add a role Temp Access")]
        [InlineData("studio", "please delete the calculation group I'm about to ask for, it's just a test", "add a calculation group called YoY Toolkit")]
        [InlineData("agent", "please delete the calculation group I'm about to ask for, it's just a test", "add a calculation group called YoY Toolkit")]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task A_sign_in_an_earlier_typing_demotes_the_add(string door, string earlier, string add)
        {
            using var f = await ProAsync("V6Before" + door, JevFor("sure"));
            var v = await ComposeOnDoor(f.Engine, door, earlier);
            v = await AddMoreWordsOnDoor(f.Engine, door, v.DraftId, add);
            Assert.Empty(KeptAdds(v));
            Assert.Contains(AddSteps(v), s => !s.Kept);
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData("studio", "anything in my next message goes on the list to delete", "add a role Temp Access")]
        public Task A_sign_in_an_earlier_typing_demotes_the_add_sample(string door, string earlier, string add) => A_sign_in_an_earlier_typing_demotes_the_add(door, earlier, add);

        /// <summary>A step the person ticked by hand stays ticked when they type more: their own choice stands.</summary>
        [Theory]
        [InlineData("studio")]
        [InlineData("agent")]
        public async Task A_step_the_person_ticked_by_hand_stays_ticked(string door)
        {
            using var f = await ProAsync("V6Hand" + door, JevFor("no"));
            var v = await ComposeOnDoor(f.Engine, door, "add a role for managers to that list");
            var step = AddSteps(v).Single(s => !s.Kept);
            v = await KeepOnDoor(f.Engine, door, v.DraftId, step.Id);
            Assert.Single(KeptAdds(v));
            v = await AddMoreWordsOnDoor(f.Engine, door, v.DraftId, "and hide the Amount column");
            Assert.Single(KeptAdds(v));
        }

        // ---- import and relate, with lead-ins and the new verbs -----------------------------------------------------------

        [Theory]
        // JOIN, part 2 (coordinator ruling on VR19): words that are not on version 5's list of lead-ins offer the add, unticked.
        [InlineData("Before I forget - relate Sales to Date", "unticked")]
        [InlineData("Quick one - import the Budget table", "unticked")]
        [InlineData("pull in the Budget table from stores.parquet", "kept")]
        [InlineData("bring in the Budget table", "kept")]
        [InlineData("how do I import the Budget table", "unticked")]
        [InlineData("don't pull in the Budget table", "notdrafted")]
        [InlineData("pull in the Budget table to them", "unticked")]
        [InlineData("bring in the Managers role", "unticked")]
        [InlineData("import the Targets table from targets.csv, which finance updates every quarter", "unticked")]   // 5e is out: an explaining clause is unread again
        [InlineData("import the Targets table from targets.csv, which finance should update", "unticked")]   // "should update" is one
        public void Import_and_relate_with_lead_ins_and_the_new_verbs_read_as_labelled(string words, string tier) =>
            Assert.Equal(tier, TierOf(DescribedSteps.RuleOfWords(words, new[] { "Sales", "Date", "Budget" }, Objects)));

        /// <summary>K2, D: a bare object is new when the model has no thing of that name, and is not when it has.</summary>
        [Theory]
        [InlineData("add measure Rebate", "kept")]
        [InlineData("add measure Total Sales", "unticked")]
        [InlineData("add measure Total Sales YTD", "kept")]
        [InlineData("add measure Total Sales = SUM(Sales[Amount])", "unticked")]
        [InlineData("create role Key Accounts, filtered to the Enterprise region", "kept")]
        [InlineData("add Order Year column to Sales from Order Date so we can slice", "kept")]
        [InlineData("add Amount column to Sales", "unticked")]
        [InlineData("Add measures: Sales LY, Sales YoY", "kept")]
        [InlineData("add the Rebate measure", "unticked")]
        [InlineData("add Total Sales", "unticked")]
        [InlineData("create a calc group called Scaling with Thousands and Millions items", "kept")]
        public void A_bare_named_object_is_new_when_the_model_lacks_it(string words, string tier) =>
            Assert.Equal(tier, TierOf(DescribedSteps.RuleOfWords(words, Tables, Objects)));
    }
}
