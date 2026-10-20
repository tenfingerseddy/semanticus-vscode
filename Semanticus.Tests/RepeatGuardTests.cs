using System;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Hero 24 D1: a page that re-asks the same explain by itself must make one live query, not thousands.</summary>
    public sealed class RepeatGuardTests
    {
        [Fact]
        public async Task A_storm_of_identical_requests_makes_one_call()
        {
            var g = new RepeatGuard(TimeSpan.FromSeconds(3));
            int calls = 0;
            Task<int> Work() => Task.Run(async () => { await Task.Delay(30); return Interlocked.Increment(ref calls); });
            var all = new Task<int>[200];
            for (int i = 0; i < all.Length; i++) all[i] = g.RunAsync("k", Work);
            var results = await Task.WhenAll(all);
            Assert.Equal(1, calls);
            Assert.All(results, r => Assert.Equal(1, r));
            // and a repeat right after the answer is served from it, not run again
            Assert.Equal(1, await g.RunAsync("k", Work));
            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task A_different_request_runs_and_an_old_answer_runs_again()
        {
            var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
            var g = new RepeatGuard(TimeSpan.FromSeconds(3), () => now);
            int calls = 0;
            Task<int> Work() => Task.FromResult(Interlocked.Increment(ref calls));
            await g.RunAsync("a", Work);
            await g.RunAsync("b", Work);
            Assert.Equal(2, calls);
            now = now.AddSeconds(4);
            Assert.Equal(3, await g.RunAsync("a", Work));
        }

        [Fact]
        public async Task A_failed_run_is_not_remembered()
        {
            var g = new RepeatGuard(TimeSpan.FromSeconds(3));
            int calls = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(() => g.RunAsync<int>("k", () => { calls++; throw new InvalidOperationException("boom"); }));
            Assert.Equal(7, await g.RunAsync("k", () => { calls++; return Task.FromResult(7); }));
            Assert.Equal(2, calls);
        }
    }
}
