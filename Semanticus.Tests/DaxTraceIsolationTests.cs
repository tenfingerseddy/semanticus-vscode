using System;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class DaxTraceIsolationTests
    {
        [Fact]
        public async Task TraceOperationsOnOneConnectionAreSerialized()
        {
            using var live = LiveConnection.ForTest("local", "trace-serial");
            var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondEntered = 0;

            var first = live.RunExclusiveAsync(async () =>
            {
                firstEntered.TrySetResult(true);
                await releaseFirst.Task.ConfigureAwait(false);
                return "profile";
            });
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var second = live.RunExclusiveAsync(() =>
            {
                Interlocked.Exchange(ref secondEntered, 1);
                return Task.FromResult("plan");
            });
            await Task.Delay(100);

            Assert.Equal(0, Volatile.Read(ref secondEntered));
            releaseFirst.TrySetResult(true);
            Assert.Equal(new[] { "profile", "plan" }, await Task.WhenAll(first, second));
            Assert.Equal(1, Volatile.Read(ref secondEntered));
        }

        [Fact]
        public async Task OrdinaryQueryWaitsForTraceLane()
        {
            using var live = LiveConnection.ForTest("local", "trace-versus-query");
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var trace = live.RunExclusiveAsync(async () =>
            {
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return true;
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var query = live.ExecuteAsync("EVALUATE { 1 }", 1, 1);
            await Task.Delay(100);
            Assert.False(query.IsCompleted);

            release.TrySetResult(true);
            Assert.True(await trace);
            var result = await query.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
        }

        [Fact]
        public async Task DifferentConnectionsRemainIndependentAndFaultsReleaseTheLane()
        {
            using var firstLive = LiveConnection.ForTest("local", "trace-a");
            using var secondLive = LiveConnection.ForTest("local", "trace-b");
            using var bothEntered = new CountdownEvent(2);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            Task<string> Start(LiveConnection live, string value) => live.RunExclusiveAsync(async () =>
            {
                bothEntered.Signal();
                await release.Task.ConfigureAwait(false);
                return value;
            });

            var first = Start(firstLive, "a");
            var second = Start(secondLive, "b");
            Assert.True(bothEntered.Wait(TimeSpan.FromSeconds(2)));
            release.TrySetResult(true);
            Assert.Equal(new[] { "a", "b" }, await Task.WhenAll(first, second));

            await Assert.ThrowsAsync<InvalidOperationException>(() => firstLive.RunExclusiveAsync<string>(
                () => Task.FromException<string>(new InvalidOperationException("capture failed"))));
            Assert.Equal("recovered", await firstLive.RunExclusiveAsync(() => Task.FromResult("recovered")));
        }
    }
}
