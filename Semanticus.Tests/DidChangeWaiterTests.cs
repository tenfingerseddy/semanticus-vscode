using System;
using System.IO;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// F-001 / T195: the RPC dual-drive paste check races because WaitNextAsync takes the next
    /// <c>model/didChange</c>, not the paste. The previous mutation's notification can still be
    /// in flight on the same pipe after its RPC response has returned (RpcServer.Broadcast does
    /// not await NotifyAsync). That straggler consumes the waiter; the paste notification then
    /// arrives with nobody listening. Lengthening the five-second deadline is forbidden: it
    /// hides a lost notification exactly as well as it waits out a slow one.
    /// The waiter is one consume-once cursor: a returned match and an ignored straggler cannot
    /// satisfy a later wait, and an overlapping wait throws instead of orphaning the first.
    /// </summary>
    public sealed class DidChangeWaiterTests
    {
        [Fact]
        public async Task A_straggler_from_the_previous_edit_is_not_observed_as_the_paste()
        {
            var waiter = new DidChangeWaiter();
            var wait = waiter.WaitAsync(n => n.Origin == "human" && n.Revision == 3);

            waiter.Observe(new ChangeNotification { Origin = "agent", Revision = 2, Label = "set DAX" });
            waiter.Observe(new ChangeNotification { Origin = "human", Revision = 3, Label = "duplicate measure:X/M" });

            var seen = await wait.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("human", seen.Origin);
            Assert.Equal(3, seen.Revision);
        }

        [Fact]
        public async Task A_matching_notification_that_arrived_before_the_waiter_is_still_observed()
        {
            var waiter = new DidChangeWaiter();
            waiter.Observe(new ChangeNotification { Origin = "human", Revision = 3, Label = "duplicate measure:X/M" });

            var seen = await waiter.WaitAsync(n => n.Origin == "human" && n.Revision == 3)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("human", seen.Origin);
            Assert.Equal(3, seen.Revision);
        }

        [Fact]
        public async Task A_returned_match_cannot_satisfy_a_later_wait()
        {
            var waiter = new DidChangeWaiter();
            var first = new ChangeNotification { Origin = "human", Revision = 3, Label = "duplicate measure:X/M" };
            waiter.Observe(first);

            var seen1 = await waiter.WaitAsync(n => n.Origin == "human" && n.Revision == 3)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(first, seen1);

            var later = waiter.WaitAsync(n => n.Origin == "human" && n.Revision == 3);
            Assert.False(later.IsCompleted);

            var second = new ChangeNotification { Origin = "human", Revision = 3, Label = "duplicate measure:X/M" };
            waiter.Observe(second);
            var seen2 = await later.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(second, seen2);
        }

        [Fact]
        public async Task An_ignored_straggler_cannot_satisfy_a_later_wait()
        {
            var waiter = new DidChangeWaiter();
            var waitPaste = waiter.WaitAsync(n => n.Origin == "human" && n.Revision == 3);
            var straggler = new ChangeNotification { Origin = "agent", Revision = 2, Label = "set DAX" };
            waiter.Observe(straggler);
            waiter.Observe(new ChangeNotification { Origin = "human", Revision = 3, Label = "duplicate measure:X/M" });
            await waitPaste.WaitAsync(TimeSpan.FromSeconds(5));

            var later = waiter.WaitAsync(n => n.Origin == "agent" && n.Revision == 2);
            Assert.False(later.IsCompleted);

            var echo = new ChangeNotification { Origin = "agent", Revision = 2, Label = "set DAX" };
            waiter.Observe(echo);
            var seen = await later.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(echo, seen);
        }

        [Fact]
        public async Task An_overlapping_wait_throws_instead_of_orphaning_the_first()
        {
            var waiter = new DidChangeWaiter();
            var first = waiter.WaitAsync(n => n.Origin == "human" && n.Revision == 3);
            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                _ = waiter.WaitAsync(n => n.Origin == "agent" && n.Revision == 2);
            });
            Assert.Contains("overlap", ex.Message, StringComparison.OrdinalIgnoreCase);

            waiter.Observe(new ChangeNotification { Origin = "human", Revision = 3, Label = "duplicate measure:X/M" });
            var seen = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("human", seen.Origin);
            Assert.Equal(3, seen.Revision);
        }

        [Fact]
        public void DidChangeWaiter_is_not_a_public_engine_type()
        {
            Assert.False(typeof(DidChangeWaiter).IsPublic);
        }

        [Fact]
        public void Paste_observation_deadline_is_still_five_seconds()
        {
            var src = File.ReadAllText(FindRpcSmoke());
            Assert.DoesNotContain("WaitNextAsync()", src, StringComparison.Ordinal);
            Assert.Contains("var n1 = await wait1.WaitAsync(TimeSpan.FromSeconds(5));", src, StringComparison.Ordinal);
            Assert.Contains("var n2 = await wait2.WaitAsync(TimeSpan.FromSeconds(5));", src, StringComparison.Ordinal);
            Assert.Contains("var nDup = await waitDup.WaitAsync(TimeSpan.FromSeconds(5));", src, StringComparison.Ordinal);
            Assert.Contains("var nLegacy = await waitLegacy.WaitAsync(TimeSpan.FromSeconds(5));", src, StringComparison.Ordinal);
            Assert.Contains("var ndoc = await waitDoc.WaitAsync(TimeSpan.FromSeconds(5));", src, StringComparison.Ordinal);
            Assert.Contains("await waitDocU.WaitAsync(TimeSpan.FromSeconds(5));", src, StringComparison.Ordinal);
            Assert.Contains("await waitCard.WaitAsync(TimeSpan.FromSeconds(5));", src, StringComparison.Ordinal);
            Assert.Contains("await waitCardU.WaitAsync(TimeSpan.FromSeconds(5));", src, StringComparison.Ordinal);
            Assert.Contains("await waitU.WaitAsync(TimeSpan.FromSeconds(5));", src, StringComparison.Ordinal);
            Assert.Contains("var synNote = await synWait.WaitAsync(TimeSpan.FromSeconds(5));", src, StringComparison.Ordinal);
            Assert.Contains("WaitNextAsync(n => n.Origin == \"human\" && n.Label == \"set DAX\")", src, StringComparison.Ordinal);
            Assert.Contains("WaitNextAsync(n => n.Origin == \"human\"", src, StringComparison.Ordinal);
        }

        private static string FindRpcSmoke()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Semanticus.RpcSmoke", "Program.cs");
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new FileNotFoundException("Could not locate Semanticus.RpcSmoke/Program.cs from " + AppContext.BaseDirectory);
        }
    }
}
