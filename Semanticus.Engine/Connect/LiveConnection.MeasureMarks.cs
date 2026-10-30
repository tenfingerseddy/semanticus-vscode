using System;
using System.Threading;

namespace Semanticus.Engine;

public sealed partial class LiveConnection
{
    // Scoped to this async query flow and this connection. Ordinary reads keep their original query.
    private static readonly AsyncLocal<MarkQueryScope> MarkQueries = new();
    private static readonly AsyncLocal<MeasureRunCancellation> MeasureRunTokens = new();
    internal IDisposable WithMeasureRunCancellation(CancellationToken token)
    {
        var scope = new MeasureRunCancellation(this, token, MeasureRunTokens.Value);
        MeasureRunTokens.Value = scope;
        return scope;
    }
    private sealed class MeasureRunCancellation : IDisposable
    {
        internal readonly LiveConnection Connection;
        internal readonly CancellationToken Token;
        private readonly MeasureRunCancellation parent;
        internal MeasureRunCancellation(LiveConnection connection, CancellationToken token, MeasureRunCancellation parent)
        { Connection = connection; Token = token; this.parent = parent; }
        public void Dispose() => MeasureRunTokens.Value = parent;
    }
    internal IDisposable WithMarkQueries(Func<string, string> transform)
    {
        var scope = new MarkQueryScope(this, transform, MarkQueries.Value);
        MarkQueries.Value = scope;
        return scope;
    }
    private string ApplyMarkQuery(string query)
    {
        var scope = MarkQueries.Value;
        return scope?.Connection == this ? scope.Transform(query) : query;
    }
    private sealed class MarkQueryScope : IDisposable
    {
        internal readonly LiveConnection Connection;
        internal readonly Func<string, string> Transform;
        private readonly MarkQueryScope parent;
        internal MarkQueryScope(LiveConnection connection, Func<string, string> transform, MarkQueryScope parent)
        { Connection = connection; Transform = transform; this.parent = parent; }
        public void Dispose() => MarkQueries.Value = parent;
    }
}
