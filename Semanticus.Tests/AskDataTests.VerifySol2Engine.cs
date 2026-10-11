// Verifier on m11/sol2-engine (1 Oct), B5 attack: a case-only rename whose FORMAT is all that changed, and a question that names the
// measure in the connected model's spelling. Both are name matches between the open copy and the connected model.

using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Query;
using Xunit;

namespace Semanticus.Tests
{
    public sealed partial class AskDataTests
    {
        [Fact]
        [Trait("VerifySol2", "B5")]
        public async Task VerifySol2_a_case_only_rename_with_a_new_format_shows_the_connected_models_format()
        {
            using var rig = await BuildAsync(ratio: true);
            await rig.Engine.RenameObjectAsync("measure:Sales/Average Price", "average price", "human");
            await rig.Engine.SetObjectPropertyAsync("measure:Sales/average price", "FormatString", "0.0%", "human");
            var a = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "average price" } });
            Assert.True(Answered(a), Seen(a));
            Assert.True(a.Formats == null || a.Formats.All(x => x == null), "the open copy's unpublished percent format was put on the connected model's number: " + string.Join(",", a.Formats ?? Array.Empty<string>()));
            Assert.Contains("average price", a.Note ?? "");
        }

        [Fact]
        [Trait("VerifySol2", "B5")]
        public async Task VerifySol2_asking_in_the_connected_spelling_reads_the_connected_formula()
        {
            using var rig = await BuildAsync(ratio: true);
            await rig.Engine.RenameObjectAsync("measure:Sales/Average Price", "average price", "human");
            await rig.Engine.SetDaxAsync("measure:Sales/average price", "DIVIDE ( [Net Sales], [Product Count] )", "human");
            var a = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Average Price" } });
            Assert.True(Answered(a), Seen(a));
            Assert.DoesNotContain("Product Count", a.Statement);
        }
    }
}
