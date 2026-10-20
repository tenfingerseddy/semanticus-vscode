// What Jev is being asked right now, in plain words with real counts (m16/jev-batch, Kane's rule: progress stays honest).
//
// A Jev request now carries a whole model's worth of one point's questions, so "Waiting for Jev, 0:08" and a counter of finished rows say
// nothing about what the wait is for. The tally holds, for each point, how many OBJECTS are out in requests that have not come back, and
// says it the way a person would: "Jev is choosing what Copilot sees in 396 fields", or "Jev is choosing 444 settings" while several points are
// out at once. The count falls as each request lands and the sentence is gone when none is out, so it never reads a number that stopped
// being true.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Semanticus.Decisions;

namespace Semanticus.Engine
{
    internal sealed class JevChoosingTally
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, int> _objects = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Objects go out (positive) or come back (negative) for a point.</summary>
        public void Add(string point, int objects)
        {
            if (string.IsNullOrEmpty(point) || objects == 0) return;
            lock (_gate)
            {
                _objects.TryGetValue(point, out var now);
                var next = Math.Max(0, now + objects);
                if (next == 0) _objects.Remove(point); else _objects[point] = next;
            }
        }

        /// <summary>The plain phrase for one point and a count, or null for a point that is not worded.</summary>
        internal static string Phrase(string point, int n)
        {
            var count = n.ToString("N0", CultureInfo.InvariantCulture);
            switch (point)
            {
                case DecisionRegistry.ColumnSummarize: return "how " + count + (n == 1 ? " column adds" : " columns add") + " up";
                case DecisionRegistry.AiSchema: return "what Copilot sees in " + count + (n == 1 ? " field" : " fields");
                case DecisionRegistry.ColumnFormat: return "formats for " + count + (n == 1 ? " column" : " columns");
                case DecisionRegistry.MeasureFolderAndFormat: return "folders and formats for " + count + (n == 1 ? " measure" : " measures");
                default: return null;
            }
        }

        private static readonly string[] Order =
        {
            DecisionRegistry.ColumnSummarize, DecisionRegistry.AiSchema, DecisionRegistry.ColumnFormat, DecisionRegistry.MeasureFolderAndFormat,
        };

        /// <summary>"Jev is choosing what Copilot sees in 396 fields" for one point, "Jev is choosing 444 settings" when several are out at once (a
        /// header slot is one short line, and the people who read it need the one real count, not three), or null when nothing is out.</summary>
        public string Words()
        {
            Dictionary<string, int> copy;
            lock (_gate) copy = new Dictionary<string, int>(_objects, StringComparer.Ordinal);
            var worded = Order.Where(copy.ContainsKey).Where(p => Phrase(p, 1) != null).ToList();
            if (worded.Count == 0) return null;
            if (worded.Count == 1) return "Jev is choosing " + Phrase(worded[0], copy[worded[0]]);
            var total = worded.Sum(p => copy[p]);
            return "Jev is choosing " + total.ToString("N0", CultureInfo.InvariantCulture) + (total == 1 ? " setting" : " settings");
        }
    }
}
