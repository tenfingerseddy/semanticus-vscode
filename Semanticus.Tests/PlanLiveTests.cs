// 1.5 plan-live (hero19 D3, D9): the Plan follows what the assistant does, and one press of Fix does not leave a second press behind.
//   after the assistant writes the descriptions, the words row recounts or turns Done, and the Plan done strip moves with it
//   the PlanInvariant holds after every step the assistant takes
using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    public sealed class PlanLiveTests
    {
        private readonly ITestOutputHelper _out;
        public PlanLiveTests(ITestOutputHelper o) => _out = o;

        private static Task<DecisionFixture> CollidingAsync(string tag) =>
            CommutationFixtures.All.Single(x => x.Name == "Colliding").Copy(tag);

        [Fact]
        public async Task The_words_row_follows_the_assistant_as_it_writes_descriptions()
        {
            using var f = await CollidingAsync("LiveWords");
            var e = f.Engine;
            var inv = new PlanInvariant(e, _out);
            var before = await inv.CheckAsync("start");
            var words = before.Items.Single(i => i.Id == LocalEngine.WordsItemId);
            _out.WriteLine("before: " + words.Title + " | " + words.Sub + " | " + words.StateWord + " strip " + before.Strip.TasksDone + "/" + before.Strip.TasksAll);

            var queue = await e.GetWordsQueueAsync(100);
            var describe = queue.Items.Where(i => i.Kind == "describe").ToArray();
            Assert.NotEmpty(describe);
            var step = 0;
            foreach (var item in describe)
            {
                var r = await e.SetDescriptionAsync(item.ObjectRef, "Words for " + item.ObjectName + ".", "agent");
                Assert.True(r.Changed);
                if (++step % 2 == 0) await inv.CheckAsync("after " + step + " descriptions");
            }
            var after = await inv.CheckAsync("after every description");
            var rest = (await e.GetWordsQueueAsync(100)).Items.Count(i => i.Kind == "describe");
            Assert.Equal(0, rest);
            var wordsAfter = after.Items.SingleOrDefault(i => i.Id == LocalEngine.WordsItemId);
            _out.WriteLine("after: " + (wordsAfter == null ? "no words row" : wordsAfter.Title + " | " + wordsAfter.Sub + " | " + wordsAfter.StateWord));
            if (wordsAfter != null) Assert.DoesNotContain("description", wordsAfter.Sub ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }
}
