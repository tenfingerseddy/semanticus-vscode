#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Adomd = Microsoft.AnalysisServices.AdomdClient;

namespace Semanticus.Engine;

public sealed partial class LiveConnection
{
    // Admit and renew once for the warm batch. Each run keeps the same ADOMD connection and cancellation scope.
    internal async Task<ResultSet[]> ExecuteWarmRunsAsync(string query, int runs, Action<int> completed = null)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            MeasureRunTokens.Value?.Connection == this ? MeasureRunTokens.Value.Token : CancellationToken.None);
        var token = cancellation.Token;
        query = ApplyMarkQuery(query);
        try { return await RunExclusiveAsync(async () => {
            var results = new List<ResultSet>(runs);
            for (var i = 0; i < runs; i++) {
                token.ThrowIfCancellationRequested();
                var result = await ExecuteWithinExclusiveAsync(query, 1, 120, token).ConfigureAwait(false);
                results.Add(result);
                if (!string.IsNullOrEmpty(result.Error)) break;
                completed?.Invoke(i + 1);
            }
            return results.ToArray();
        }).ConfigureAwait(false); }
        catch (Azure.Identity.AuthenticationFailedException) {
            return new[] { new ResultSet { Error = "Silent sign-in could not renew this connection. Sign in again to continue.", AuthFailed = true, Query = query } };
        }
    }

    /// <summary>Open a separate query lane with this connection's identity. This never starts interactive sign-in.</summary>
    public Task<LiveConnection> OpenCheckConnectionAsync(CancellationToken ct = default) => RunWithLifetimeLeaseAsync(async () =>
    {
        ct.ThrowIfCancellationRequested();
        LiveConnection sibling;
        if (_executeMultiForTest != null) sibling = ForMultiTest(_executeMultiForTest);
        else if (_executeForTest != null) sibling = ForTest(Kind, DataSource, Database, _executeForTest);
        else
        {
            var token = await FreshTokenAsync(false).ConfigureAwait(false);
            var settings = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = _connectionString };
            if (!string.IsNullOrEmpty(Database)) settings["Initial Catalog"] = Database;
            sibling = await OpenAsync(Kind, DataSource, settings.ConnectionString,
                string.IsNullOrEmpty(token.Token) ? null : token, _silentCredential).ConfigureAwait(false);
        }
        sibling.Account = Account;
        sibling.DesktopName = DesktopName;
        sibling.DesktopPath = DesktopPath;
        if (ct.IsCancellationRequested) { sibling.Dispose(); ct.ThrowIfCancellationRequested(); }
        return sibling;
    });

    // B10 measures lane admission, separately from network and server time.
    internal Task<ResultSet> ExecuteStartedAsync(string query, int maxRows, int timeout, Action started,
        CancellationToken ct = default) => RunExclusiveAsync(() => _queryThread.RunAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            started();
            return _executeForTest != null ? RunTestExecute(query, maxRows, ct) : Execute(query, maxRows, timeout, ct);
        }));
}
