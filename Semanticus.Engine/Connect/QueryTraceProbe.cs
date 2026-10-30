#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Semanticus.Engine;

// Query timing probe: SEMANTICUS_QUERY_TRACE=<folder> writes every multi-set command's text and its
// timing, one JSON line per command in trace.jsonl and the text in q<n>.dax. Off (no cost) when the variable is unset.
internal static class QueryTraceProbe
{
    private static readonly string? Folder = Environment.GetEnvironmentVariable("SEMANTICUS_QUERY_TRACE");
    private static readonly object Gate = new();
    private static int count;

    internal static void Multi(string query, MultiResult result, long elapsedMs)
    {
        if (string.IsNullOrEmpty(Folder)) return;
        try
        {
            var n = Interlocked.Increment(ref count);
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, $"q{n}.dax"), query);
            var line = JsonSerializer.Serialize(new
            {
                n, at = DateTimeOffset.UtcNow.ToString("O"), elapsedMs, result.Commands, chars = query.Length,
                sets = result.Sets.Select(s => new { id = s.Spec.Id, cap = s.Spec.MaxRows, rows = s.Result.Rows?.Length ?? 0,
                    error = s.Result.Error, truncated = s.Result.Truncated }).ToArray(),
            });
            lock (Gate) File.AppendAllText(Path.Combine(Folder, "trace.jsonl"), line + Environment.NewLine);
        }
        catch { /* a probe never fails a check */ }
    }
}
