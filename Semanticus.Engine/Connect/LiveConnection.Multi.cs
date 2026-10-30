#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Adomd = Microsoft.AnalysisServices.AdomdClient;

namespace Semanticus.Engine;

public sealed record MultiSetResult(MultiSetSpec Spec, ResultSet Result);
public sealed record MultiResult(MultiSetResult[] Sets, long ElapsedMs, int Commands)
{
    // A server failure poisons the ADOMD reader. These sets were never read, unlike a capped or duplicate set.
    internal int[] Unread { get; init; } = Array.Empty<int>();
}

public sealed partial class LiveConnection
{
    private Func<string, CancellationToken, IDataReader>? _executeMultiForTest;

    internal static LiveConnection ForMultiTest(Func<string, CancellationToken, IDataReader> execute) =>
        new("test", "Contoso", new Adomd.AdomdConnection(), "") { _executeMultiForTest = execute };

    /// <summary>Independently bounded sets, with one recovery command for sets lost after a server error.</summary>
    public async Task<MultiResult> ExecuteMultiAsync(string query, IReadOnlyList<MultiSetSpec> specs,
        int commandTimeoutSeconds = 10, CancellationToken ct = default)
    {
        if (specs.Count == 0) return new MultiResult(Array.Empty<MultiSetResult>(), 0, 0);
        if (specs.Any(s => s.MaxRows < 1)) throw new ArgumentOutOfRangeException(nameof(specs));
        try
        {
            return await RunExclusiveAsync(() => _queryThread.RunAsync(() =>
                ExecuteMulti(query, specs, commandTimeoutSeconds, ct))).ConfigureAwait(false);
        }
        catch (Azure.Identity.AuthenticationFailedException)
        {
            return MultiFailure(specs, "Silent sign-in could not renew this connection. Sign in again to continue.", false, true);
        }
    }

    private MultiResult ExecuteMulti(string query, IReadOnlyList<MultiSetSpec> specs, int timeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Interlocked.Exchange(ref _currentCts, linked);
        var watch = Stopwatch.StartNew();
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            var result = ExecuteOnce(query, specs);
            if (result.Unread.Length > 0 && !linked.IsCancellationRequested
                && !result.Sets.Any(s => s.Result.AuthFailed || s.Result.Cancelled))
            {
                var starts = EvaluateOffsets(query);
                if (starts.Length == specs.Count)
                {
                    var retry = result.Unread;
                    var retryQuery = query.Substring(0, starts[0]) + string.Join(Environment.NewLine, retry.Select(i =>
                        query.Substring(starts[i], (i + 1 < starts.Length ? starts[i + 1] : query.Length) - starts[i])));
                    // Dispose the failed reader before making the second command. Never retry recursively.
                    var recovered = ExecuteOnce(retryQuery, retry.Select(i => specs[i]).ToArray());
                    for (var i = 0; i < retry.Length; i++) result.Sets[retry[i]] = recovered.Sets[i];
                    result = result with { Commands = 2, Unread = Array.Empty<int>() };
                }
            }
            foreach (var set in result.Sets) set.Result.Query = query;
            QueryTraceProbe.Multi(query, result, watch.ElapsedMilliseconds);
            return result with { ElapsedMs = watch.ElapsedMilliseconds };

            MultiResult ExecuteOnce(string commandText, IReadOnlyList<MultiSetSpec> commandSpecs)
            {
                try
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (_executeMultiForTest != null)
                    {
                        using var testReader = _executeMultiForTest(commandText, linked.Token);
                        return ReadMulti(testReader, commandSpecs, linked.Token);
                    }
                    if (_conn.State != ConnectionState.Open && !ReopenOnQueryThread())
                        return MultiFailure(commandSpecs, ConnectionDroppedLine, false, false);
                    using var command = new Adomd.AdomdCommand(commandText, _conn) { CommandTimeout = timeout <= 0 ? 10 : timeout };
                    using var cancel = linked.Token.Register(() => { try { command.Cancel(); } catch { } });
                    using var reader = command.ExecuteReader();
                    return ReadMulti(reader, commandSpecs, linked.Token);
                }
                catch (Exception ex)
                {
                    var stopped = linked.IsCancellationRequested || ex is OperationCanceledException;
                    var auth = XmlaAuthHint.IsQueryAuthFailure(ex.Message);
                    var failure = MultiFailure(commandSpecs, stopped ? ResultSet.StoppedMessage
                        : DaxErrorText.Plain(ScrubSecrets(ex.Message)), stopped, auth);
                    var bad = stopped || auth ? -1 : FailedPart(commandText, ex.Message, commandSpecs.Count);
                    return bad < 0 ? failure : failure with {
                        Unread = Enumerable.Range(0, commandSpecs.Count).Where(i => i != bad).ToArray()
                    };
                }
            }
        }
        catch (Exception ex)
        {
            var cancelled = linked.IsCancellationRequested || ex is OperationCanceledException;
            return MultiFailure(specs, cancelled ? ResultSet.StoppedMessage : DaxErrorText.Plain(ScrubSecrets(ex.Message)),
                cancelled, XmlaAuthHint.IsQueryAuthFailure(ex.Message)) with { ElapsedMs = watch.ElapsedMilliseconds };
        }
        finally { Interlocked.CompareExchange(ref _currentCts, null, linked); }
    }

    private static MultiResult MultiFailure(IReadOnlyList<MultiSetSpec> specs, string error, bool cancelled, bool authFailed) =>
        new(specs.Select(spec => new MultiSetResult(spec,
            new ResultSet { Error = error, Cancelled = cancelled, AuthFailed = authFailed })).ToArray(), 0, 1);

    private static int[] EvaluateOffsets(string query) => DaxScan.Tokenize(query)
        .Where(t => t.Kind == DaxScan.Kind.Word && t.Depth == 0
            && string.Equals(t.Text, "EVALUATE", StringComparison.OrdinalIgnoreCase)).Select(t => t.Start).ToArray();

    private static int FailedPart(string query, string error, int count)
    {
        var position = Regex.Match(error, "Query [(]([0-9]+), *([0-9]+)[)]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!position.Success
            || !int.TryParse(position.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var line)
            || !int.TryParse(position.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var column)
            || line < 1 || column < 1) return -1;
        var offset = 0;
        for (var current = 1; current < line; current++)
        {
            var next = query.IndexOf((char)10, offset);
            if (next < 0) return -1;
            offset = next + 1;
        }
        var end = query.IndexOf((char)10, offset);
        if (column - 1 > (end < 0 ? query.Length : end) - offset) return -1;
        offset += column - 1;
        var starts = EvaluateOffsets(query);
        // An error in DEFINE has no isolated EVALUATE to remove.
        if (starts.Length != count || starts.Length == 0 || offset < starts[0]) return -1;
        return Array.FindLastIndex(starts, start => start <= offset);
    }

    internal static MultiResult ReadMulti(IDataReader reader, IReadOnlyList<MultiSetSpec> specs,
        CancellationToken ct = default)
    {
        var sets = new List<MultiSetResult>();
        var unread = new List<int>();
        var available = true;
        string? nextError = null;
        var poisoned = false;
        foreach (var spec in specs)
        {
            if (ct.IsCancellationRequested)
            {
                sets.Add(new MultiSetResult(spec, ResultSet.FromCancelled()));
                continue;
            }
            if (!available)
            {
                sets.Add(new MultiSetResult(spec, ResultSet.FromError(nextError ?? "The command did not return this result set.")));
                if (poisoned) unread.Add(sets.Count - 1);
                continue;
            }
            ResultSet result;
            var serverFailed = false;
            try
            {
                var columns = Enumerable.Range(0, reader.FieldCount).Select(i =>
                    new ColumnDef { Name = reader.GetName(i), Type = reader.GetFieldType(i)?.Name }).ToArray();
                var rows = new List<object[]>();
                var keys = new HashSet<string>(StringComparer.Ordinal);
                string? error = null;
                var capped = false;
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    // Read cap + 1, then discard the whole set. NextResult skips the remaining server rows.
                    if (rows.Count == spec.MaxRows) { capped = true; error = "Too many rows to check this place."; break; }
                    var row = new object[reader.FieldCount];
                    for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null! : reader.GetValue(i);
                    if (spec.KeyColumns != null && !keys.Add(ResultKey(row, spec.KeyColumns)))
                    {
                        error = "The result has a duplicate cell key.";
                        break;
                    }
                    rows.Add(row);
                }
                result = new ResultSet { Columns = columns, Rows = error == null ? rows.ToArray() : Array.Empty<object[]>(),
                    RowCount = error == null ? rows.Count : 0, Truncated = capped, Error = error };
            }
            catch (Exception ex)
            {
                result = ct.IsCancellationRequested || ex is OperationCanceledException ? ResultSet.FromCancelled()
                    : ResultSet.FromError(ScrubSecrets(ex.Message));
                result.AuthFailed = XmlaAuthHint.IsQueryAuthFailure(ex.Message);
                serverFailed = true;
            }
            sets.Add(new MultiSetResult(spec, result));
            if (serverFailed)
            {
                available = false;
                poisoned = true;
                nextError = "The earlier server error prevented this result set from being read.";
                continue;
            }
            try { available = reader.NextResult(); }
            catch (Exception ex)
            {
                // NextResult can surface the next set's error before its first Read.
                available = false;
                nextError = DaxErrorText.Plain(ScrubSecrets(ex.Message));
                if (sets.Count < specs.Count)
                {
                    sets.Add(new MultiSetResult(specs[sets.Count], ResultSet.FromError(nextError)));
                    unread.AddRange(Enumerable.Range(sets.Count, specs.Count - sets.Count));
                    foreach (var remaining in specs.Skip(sets.Count))
                        sets.Add(new MultiSetResult(remaining, ResultSet.FromError("The earlier server error prevented this result set from being read.")));
                }
                break;
            }
        }
        return new MultiResult(sets.ToArray(), 0, 1) { Unread = unread.ToArray() };
    }

    private static string ResultKey(object[] row, int[] columns)
    {
        var key = new StringBuilder();
        foreach (var column in columns)
        {
            var value = row[column];
            if (value == null) { key.Append("B;"); continue; }
            var text = value is IFormattable number ? number.ToString(null, CultureInfo.InvariantCulture) : value.ToString();
            key.Append(value.GetType().FullName).Append(':').Append(text!.Length).Append(':').Append(text).Append(';');
        }
        return key.ToString();
    }
}
