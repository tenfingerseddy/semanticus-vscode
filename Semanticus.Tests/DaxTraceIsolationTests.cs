using System;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class DaxTraceIsolationTests
    {
        private sealed class QueryCredential : Azure.Core.TokenCredential
        {
            public int Calls;
            public bool Fail;
            public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext context, CancellationToken ct)
            {
                Interlocked.Increment(ref Calls);
                if (Fail) throw new Azure.Identity.AuthenticationFailedException("Silent authentication unavailable");
                return new Azure.Core.AccessToken("renewed-query-account", DateTimeOffset.UtcNow.AddHours(1));
            }
            public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext context, CancellationToken ct)
                => ValueTask.FromResult(GetToken(context, ct));
        }

        // Kane, 17 Oct: "no query plan seems to return". On the Power BI service the DAXQueryPlan (112) and DAXEvaluationLog (135)
        // events are only sent to a trace that collects the DatabaseName column (id 28): measured on the real Contoso Sales service
        // 17 Oct, the same query gave no plan with the old columns and a logical + physical plan with this one added. DAX Studio 2.9.0
        // added the same column "to work with the new Power BI XMLA endpoint".
        [Fact]
        public void Plan_and_log_events_collect_the_database_name_the_service_needs_to_send_them()
        {
            Assert.Contains(Microsoft.AnalysisServices.TraceColumn.DatabaseName, DaxTrace.MarkerCols);
            Assert.Contains(Microsoft.AnalysisServices.TraceColumn.DatabaseName, DaxTrace.EvalLogCols);
        }

        // Kane, 17 Oct: the page often showed the warm-up ping's plan ("TableCtor") as the query's plan. The ping's late plan and QueryEnd
        // landed in the real window, and a late ping QueryEnd stopped the wait before the query's own plan arrived.
        [Fact]
        public void A_ping_end_never_counts_as_the_querys_end_and_the_pings_plan_is_dropped()
        {
            var c = new PlanCollector("semwarm_ab12");
            c.OnPlan(false, "TableCtor: ping logical");
            c.OnQueryEnd("EVALUATE ROW(\"semwarm_ab12_1\", 1)");
            c.OnPlan(true, "TableCtor: late ping physical semwarm_ab12_1");      // late and still tagged: dropped by text
            Assert.False(c.GotQueryEnd);
            Assert.Equal(1, c.PingEnds);
            Assert.Equal("", c.Logical);
            Assert.Equal("", c.Physical);

            c.OnPlan(false, "GroupBy: the query's logical");
            c.OnQueryEnd("EVALUATE SUMMARIZECOLUMNS('Date'[Year])");
            Assert.True(c.GotQueryEnd);
            Assert.False(c.GotBothPlans);                                         // its physical plan has not come yet
            c.OnPlan(true, "Cache: the query's physical");
            Assert.True(c.GotBothPlans);
            Assert.Equal("GroupBy: the query's logical", c.Logical);
            Assert.Equal("Cache: the query's physical", c.Physical);
        }

        [Fact]
        public async Task Query_and_profile_lanes_renew_their_own_expiring_identity_before_work()
        {
            using var live = LiveConnection.ForTest("xmla", "query-target", "query-model", _ => new ResultSet());
            var credential = new QueryCredential();
            live.ConfigureAuthentication(new Azure.Core.AccessToken("old-query-account", DateTimeOffset.UtcNow.AddMinutes(1)), credential);

            var callsAtProfileStart = await live.RunExclusiveAsync(() => Task.FromResult(credential.Calls));
            Assert.Equal(1, callsAtProfileStart);
            await live.ExecuteAsync("EVALUATE { 1 }", 1, 30);
            Assert.Equal(1, credential.Calls);
        }

        [Fact]
        public async Task Fresh_query_token_needs_no_authentication_round_trip()
        {
            using var live = LiveConnection.ForTest("xmla", "query-target", "query-model", _ => new ResultSet());
            var credential = new QueryCredential();
            live.ConfigureAuthentication(new Azure.Core.AccessToken("valid-query-account", DateTimeOffset.UtcNow.AddHours(1)), credential);
            await live.ExecuteAsync("EVALUATE { 1 }", 1, 30);
            Assert.Equal(0, credential.Calls);
        }

        [Fact]
        public async Task Failed_early_renewal_keeps_a_valid_query_token_usable()
        {
            using var live = LiveConnection.ForTest("xmla", "query-target", "query-model", _ => new ResultSet());
            live.ConfigureAuthentication(new Azure.Core.AccessToken("valid-query-account", DateTimeOffset.UtcNow.AddMinutes(1)), new QueryCredential { Fail = true });
            var result = await live.ExecuteAsync("EVALUATE { 1 }", 1, 30);
            Assert.False(result.AuthFailed);
            Assert.Null(result.Error);
        }

        [Fact]
        public async Task Failed_expired_renewal_returns_the_sign_in_result_without_running_a_query()
        {
            var queryCalls = 0;
            using var live = LiveConnection.ForTest("xmla", "query-target", "query-model", _ => { queryCalls++; return new ResultSet(); });
            live.ConfigureAuthentication(new Azure.Core.AccessToken("expired-query-account", DateTimeOffset.UtcNow.AddSeconds(2)), new QueryCredential { Fail = true });
            await Task.Delay(2200);
            var result = await live.ExecuteAsync("EVALUATE { 1 }", 1, 30);
            Assert.True(result.AuthFailed);
            Assert.Equal(0, queryCalls);
        }

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
