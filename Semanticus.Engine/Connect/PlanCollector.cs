using System;
using System.Diagnostics;
using System.Text;

namespace Semanticus.Engine
{
    /// <summary>
    /// Sorts the trace events of one plan capture into "the warm-up ping's" and "the query's own". The ping carries a unique tag in its
    /// query text, so its QueryEnd is told apart by TextData; a session's events arrive in order, so everything before the last ping's
    /// QueryEnd belongs to a ping and is dropped, and a later plan whose text still carries the tag is dropped too.
    /// </summary>
    internal sealed class PlanCollector
    {
        private const int PlanCap = 20000;
        private readonly object _gate = new();
        private readonly StringBuilder _logical = new();
        private readonly StringBuilder _physical = new();
        private long _lastEventTicks = Stopwatch.GetTimestamp();
        private int _pingEnds;
        private bool _queryEnd;

        internal PlanCollector(string pingTag) { PingTag = pingTag; }

        internal string PingTag { get; }
        internal int PingEnds { get { lock (_gate) return _pingEnds; } }
        internal bool GotQueryEnd { get { lock (_gate) return _queryEnd; } }
        internal string Logical { get { lock (_gate) return _logical.ToString().TrimEnd(); } }
        internal string Physical { get { lock (_gate) return _physical.ToString().TrimEnd(); } }
        internal bool GotBothPlans { get { lock (_gate) return _logical.Length > 0 && _physical.Length > 0; } }
        internal TimeSpan SinceLastEvent { get { lock (_gate) return TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - _lastEventTicks) / (double)Stopwatch.Frequency); } }

        private bool IsPing(string text) => !string.IsNullOrEmpty(text) && text.IndexOf(PingTag, StringComparison.Ordinal) >= 0;

        internal void OnQueryEnd(string text)
        {
            lock (_gate)
            {
                _lastEventTicks = Stopwatch.GetTimestamp();
                if (IsPing(text)) { _pingEnds++; _logical.Clear(); _physical.Clear(); _queryEnd = false; }
                else _queryEnd = true;
            }
        }

        internal void OnPlan(bool physical, string text)
        {
            lock (_gate)
            {
                _lastEventTicks = Stopwatch.GetTimestamp();
                if (string.IsNullOrEmpty(text) || IsPing(text)) return;
                var sb = physical ? _physical : _logical;
                if (sb.Length < PlanCap) sb.AppendLine(text);
            }
        }

        /// <summary>The caller starts the query itself after the pings: forget anything seen so far (a ping that never reported its end).</summary>
        internal void Reset() { lock (_gate) { _logical.Clear(); _physical.Clear(); _queryEnd = false; _lastEventTicks = Stopwatch.GetTimestamp(); } }
    }
}
