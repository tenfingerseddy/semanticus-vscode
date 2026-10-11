using System;
using System.Threading;

namespace Semanticus.Engine
{
    /// <summary>The refusal of a write whose approval is stale: what the person was asked about is no longer what is on disk. A distinct type so a
    /// store that swallows its own I/O failures (they return false or null by contract) can let this one through.</summary>
    internal sealed class StaleApprovalException : InvalidOperationException
    {
        public StaleApprovalException(string message) : base(message) { }
    }

    /// <summary>
    /// The check under a store's own lock (join 6, area B). A machine registry (connections, SQL sources, a model's SQL endpoint) is written by
    /// small static classes that know nothing of the gate. The engine arms this around the call; the registry calls <see cref="Check"/> with its file path
    /// after it holds its in-process and cross-process locks and before it reads for writing, and the armed callback compares the file with what the
    /// person was asked about. It fires once, at the first write inside the armed scope: a later write in the same operation is the operation's own
    /// doing, not something that changed behind the person's back. Nothing armed means nothing happens, so every other caller is untouched.
    /// </summary>
    internal static class RegistryWriteGuard
    {
        private sealed class Armed { public Action<string> Before; public string File; }
        private static readonly AsyncLocal<Armed> Current = new();

        /// <summary>Arm <paramref name="before"/> for the first registry write in this flow. Dispose to disarm.</summary>
        public static IDisposable Around(Action<string> before, string onlyFile = null)
        {
            var previous = Current.Value;
            Current.Value = new Armed { Before = before, File = onlyFile };
            return new Scope(previous);
        }

        public static void Check(string file)
        {
            var armed = Current.Value;
            var before = armed?.Before;
            if (before == null || (armed.File != null && !string.Equals(armed.File, file, StringComparison.Ordinal))) return;
            armed.Before = null;
            before(file);
        }

        private sealed class Scope : IDisposable
        {
            private readonly Armed _previous;
            public Scope(Armed previous) { _previous = previous; }
            public void Dispose() => Current.Value = _previous;
        }
    }
}
