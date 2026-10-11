// Sol 6.1's review of the M11 delta (1 Oct), finding B5 (second strike of 3.7): a measure the open copy only RENAMED IN CASE kept its
// local formula in the reading while the connected model ran its own. DAX names do not care about case, so every name match between
// the open copy and the connected model must not either.

using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Query;
using Xunit;

namespace Semanticus.Tests
{
    public sealed partial class AskDataTests
    {
        [Fact]
        [Trait("Sol2", "B5")]
        public async Task Sol2_a_case_only_rename_with_a_new_formula_is_read_from_the_connected_model()
        {
            using var rig = await BuildAsync(ratio: true);
            // The connected model still holds "Average Price" as Net Sales divided by Units. The open copy renames it in case only
            // and changes its formula, and nothing is published.
            await rig.Engine.RenameObjectAsync("measure:Sales/Average Price", "average price", "human");
            await rig.Engine.SetDaxAsync("measure:Sales/average price", "DIVIDE ( [Net Sales], [Product Count] )", "human");
            var a = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "average price" } });
            Assert.True(Answered(a), Seen(a));
            Assert.Contains("Net Sales divided by Units", a.Statement);
            Assert.DoesNotContain("Product Count", a.Statement);
            Assert.Contains("average price", a.Note);
            Assert.Contains("connected model", a.Note);
        }

        [Fact]
        [Trait("Sol2", "B5")]
        public async Task Sol2_a_connected_model_that_spells_the_name_differently_is_still_the_same_measure_for_an_unchanged_formula()
        {
            using var rig = await BuildAsync(ratio: true);
            // Same formula, different case on the connected side: nothing changed, so nothing extra is said.
            rig.Data.Connected = rig.Data.Connected.ToDictionary(kv => kv.Key.ToUpperInvariant(), kv => kv.Value);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Average Price" } });
            Assert.True(Answered(a), Seen(a));
            Assert.Null(a.Note);
        }
    }
}
