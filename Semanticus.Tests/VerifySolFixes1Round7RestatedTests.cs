// verify: six rows of VerifySolFixes1Round5DefectTests restated for m11/sol-fixes-1 round 7 (79769b3d, THE ADDING RULE
// version 5). VR9's "aren't needed" words, VR11 past the limit and VR11b, each on both doors, asserted that no step that adds
// is drafted at all, kept or dropped. Version 5 item 2 offers such an add UNTICKED, with the person's words and one true line
// on why, and the harm is a KEPT add of the thing the words take away, put on a list to take away, or take back. An unticked
// step never runs unless the person ticks it, so the verifier agrees these rows are not harm: their letter is now "no KEPT
// add". The six old rows are retired from the Round 5 file in the same commit. The Jev oracle says no to every step, as the
// Round 5 file's does, so every clause takes the Plan's fallback, where the rule decides.

using System;
using System.Collections.Generic;
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
    public sealed class VerifySolFixes1Round7RestatedTests
    {
        private sealed class JevSaysNo : IDecisionOracle
        {
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
                    sb.Append(JsonSerializer.Serialize(q.Name)).Append(':').Append(q.Kind == DecisionQuestionKind.Noul
                        ? "{\"type\":\"noul\",\"noul\":0.02}"
                        : q.Name.EndsWith(":wanted", StringComparison.Ordinal) ? "{\"type\":\"choice\",\"choice\":\"yes\",\"confidence\":0.95}"   // join part 2: the last question fails closed, so a script answers it yes
                        : "{\"type\":\"choice\",\"choice\":\"none\",\"confidence\":0.95}");
                }
                sb.Append("}}]}");
                return new RecordedDecisionOracle(sb.ToString(), "jev-vsf7r").AskAsync(request, ct);
            }
        }

        private static async Task<DecisionFixture> ProAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync(file);
            await f.SaveAsAsync(file);
            await f.ConsentAsync();
            f.Engine.UseDecisionsForTest(new DecisionService(new JevSaysNo(), new DecisionBudget()));
            return f;
        }

        private static string File(string name) => name + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bim";

        private static Task<DraftView> ComposeOnDoor(LocalEngine engine, string door, string words) =>
            door == "studio" ? new EngineRpcTarget(engine).composeTask(words: words)
                             : McpTools.ComposeTask(engine, words: words);

        private static Task<DraftView> AddMoreWordsOnDoor(LocalEngine engine, string door, string draftId, string words) =>
            door == "studio" ? new EngineRpcTarget(engine).editDraft(draftId, "refine", text: words)
                             : McpTools.EditDraft(engine, draftId, "refine", text: words);

        /// <summary>The Plan's steps that add (Add a measure, a role, a column, a calculation group, Relate two tables,
        /// Import a table) that are KEPT (ticked), by title.</summary>
        private static string[] KeptAdds(DraftView v) =>
            v.Steps.Concat(v.After ?? Array.Empty<DraftStepView>())
                .Where(s => s.Kept && new[] { "add-measure", "relate", "import", "add-column", "calc-group", "role" }
                    .Any(a => s.Id == a || s.Id.StartsWith(a + "-", StringComparison.Ordinal) && s.Id.Substring(a.Length + 1).All(char.IsDigit)))
                .Select(s => s.Title).ToArray();

        public static IEnumerable<object[]> Doors() => new[] { new object[] { "studio" }, new object[] { "agent" } };

        /// <summary>VR9, restated: "add the Managers role to the roles that aren't needed" puts the role with the roles that
        /// are not needed. No Add a role is kept, on either door.</summary>
        [Theory]
        [MemberData(nameof(Doors))]
        public async Task VR9r_a_role_put_with_the_roles_not_needed_is_never_a_kept_add(string door)
        {
            using var f = await ProAsync(File("VR9r"));
            var v = await ComposeOnDoor(f.Engine, door, "add the Managers role to the roles that aren't needed");
            Assert.True(KeptAdds(v).Length == 0, string.Join(", ", KeptAdds(v)));
        }

        /// <summary>VR11 past the limit, restated: thirteen clauses ask to remove measures and the thirteenth adds one "to that
        /// list too". No Add a measure is kept, when composed or after Add more words, on either door.</summary>
        [Theory]
        [MemberData(nameof(Doors))]
        public async Task VR11r_an_add_to_the_remove_list_past_the_limit_is_never_a_kept_add(string door)
        {
            using var f = await ProAsync(File("VR11Lr"));
            var v = await ComposeOnDoor(f.Engine, door, "Remove Total Sales, Total Cost, Average Price, Margin, Units, Returns, Budget, "
                + "Profit, Discount, Tax, Freight, Rebate, and add the Net Sales measure to that list too");
            Assert.True(KeptAdds(v).Length == 0, "composed => " + string.Join(", ", KeptAdds(v)));
            v = await AddMoreWordsOnDoor(f.Engine, door, v.DraftId, "and hide the Margin measure");
            Assert.True(KeptAdds(v).Length == 0, "after Add more words => " + string.Join(", ", KeptAdds(v)));
        }

        /// <summary>VR11b, restated: "add a measure for margin and the old role should be removed" typed before permission and
        /// read at Add more words. No Add a measure is kept, on either door.</summary>
        [Theory]
        [MemberData(nameof(Doors))]
        public async Task VR11br_an_add_typed_before_permission_with_a_take_away_after_it_is_never_a_kept_add(string door)
        {
            using var f = await ProAsync(File("VR11br"));
            await f.Engine.WithdrawConsentAsync("human");
            var v = await ComposeOnDoor(f.Engine, door, "add a measure for margin and the old role should be removed");
            await f.ConsentAsync();
            v = await AddMoreWordsOnDoor(f.Engine, door, v.DraftId, "and hide the Margin measure");
            Assert.True(KeptAdds(v).Length == 0, string.Join(", ", KeptAdds(v)));
        }
    }
}
