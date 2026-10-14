// What a Jev row is worth, in AI readiness points ("+4 AI ready"), on the row's own Value (1.5, Kane's pick A of the Jev plan).
//
// THE NUMBER IS MEASURED, NEVER ESTIMATED. A Jev line is not a readiness finding (it has no rule id the scorecard knows), so a sum of
// ReadinessAnalyzer.Lifts over the row's rules would have to guess which findings a line clears. Instead the row's kept lines are
// REHEARSED: the real executor writes them inside one batch, the real analyzer scores the changed model, and the batch rolls back
// (the same rehearsal the Plan's grade projection uses, Session.MutateAsync under a DryRunCollector). The worth is the score after
// minus the score before, so applying the row moves the score by exactly that, and a row that moves it by less than half a point
// says nothing rather than "+0".
//
// IT NEVER MAKES THE PLAN WAIT. A rehearsal is a full scan per row, so the compose only READS what is known and starts one background
// pass for the rows it does not know yet; when the pass lands the page is pushed the plan again (TellThePlanMoved) and the number
// appears. A number belongs to one model revision and one set of kept lines: any change to either makes it unknown again, so a
// number is never shown for a model or a tick it was not worked out on.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        private sealed class RowWorthBook
        {
            public string SessionId;
            public long Revision;
            /// <summary>The points each row (by its worth key) moved the score in rehearsal.</summary>
            public readonly Dictionary<string, double> Points = new Dictionary<string, double>(StringComparer.Ordinal);
            /// <summary>Rows a pass was started for, so a row whose rehearsal failed is not tried again on every compose.</summary>
            public readonly HashSet<string> Tried = new HashSet<string>(StringComparer.Ordinal);
            public Task Running = Task.CompletedTask;
        }

        private readonly object _rowWorthLock = new object();
        private RowWorthBook _rowWorth;

        /// <summary>The background pass for the rows' worth, or a finished task when none is running. A test waits on it instead of sleeping.</summary>
        internal Task RowWorthForTest { get { lock (_rowWorthLock) return _rowWorth?.Running ?? Task.CompletedTask; } }

        /// <summary>The smallest worth a row says, in points. Under it the row says nothing: "+0 AI ready" is not news.</summary>
        internal const double RowWorthFloor = 0.5;

        /// <summary>What the row says it is worth: "+4 AI ready", or null when it is under the floor.</summary>
        internal static string RowWorthWords(double points)
        {
            var n = (int)Math.Round(points, MidpointRounding.AwayFromZero);
            return points >= RowWorthFloor && n >= 1 ? "+" + n.ToString(CultureInfo.InvariantCulture) + " AI ready" : null;
        }

        private static string WorthKey(string itemId, IEnumerable<ChangeItem> kept) =>
            itemId + "␟" + string.Join("␞", kept.Select(JevFamily.KeyOf));

        private static bool IsKeptLine(ChangeItem l) => string.Equals(l.Status, "approved", StringComparison.OrdinalIgnoreCase);

        /// <summary>Stamp the worth the compose already knows onto the Jev rows, and start a pass for the ones it does not.</summary>
        private void StampRowWorth(PlanMemoryBinding binding, Session s, IEnumerable<ModelPlanItem> items, ILookup<string, ChangeItem> linesByItem)
        {
            var rows = new List<(ModelPlanItem Item, string Key, List<ChangeItem> Kept)>();
            foreach (var item in items)
            {
                if (!JevFamily.IsJevItem(item.Id) || string.Equals(item.Status, ModelPlanStatuses.Skipped, StringComparison.Ordinal)) continue;
                var kept = linesByItem[item.Id].Where(IsKeptLine).ToList();
                if (kept.Count > 0) rows.Add((item, WorthKey(item.Id, kept), kept));
            }
            if (rows.Count == 0) return;

            lock (_rowWorthLock)
            {
                var revision = s.Revision;
                if (_rowWorth == null || _rowWorth.SessionId != s.Id || _rowWorth.Revision != revision)
                    _rowWorth = new RowWorthBook { SessionId = s.Id, Revision = revision };
                var book = _rowWorth;
                var missing = new List<(string Key, List<ChangeItem> Lines)>();
                foreach (var (item, key, kept) in rows)
                {
                    if (book.Points.TryGetValue(key, out var points)) item.Value = RowWorthWords(points) ?? string.Empty;
                    else if (!book.Tried.Contains(key)) missing.Add((key, kept.Select(l => l.Clone()).ToList()));
                }
                if (missing.Count == 0 || !book.Running.IsCompleted) return;
                book.Tried.UnionWith(missing.Select(m => m.Key));
                book.Running = Task.Run(() => RehearseRowsAsync(binding, s, book, missing));
            }
        }

        private async Task RehearseRowsAsync(PlanMemoryBinding binding, Session s, RowWorthBook book, List<(string Key, List<ChangeItem> Lines)> missing)
        {
            var landed = false;
            try
            {
                var before = await s.ReadAsync(m => new ReadinessAnalyzer().Analyze(m).Overall).ConfigureAwait(false);
                foreach (var (key, lines) in missing)
                {
                    var previous = DryRunScope.Current;
                    try
                    {
                        DryRunScope.Current = new DryRunCollector();
                        double? after = null;
                        await s.MutateAsync("engine", "plan: rehearse one row", m =>
                        {
                            PlanLineExecutor.ApplyInBatch(this, m, s, lines);
                            after = new ReadinessAnalyzer().Analyze(m).Overall;
                        }).ConfigureAwait(false);
                        // A model that moved while this ran is a different model: the number would be about nothing.
                        lock (_rowWorthLock)
                            if (after.HasValue && ReferenceEquals(_rowWorth, book) && s.Revision == book.Revision)
                            { book.Points[key] = after.Value - before; landed = true; }
                    }
                    catch { /* a rehearsal that fails has rolled back; the row simply says nothing */ }
                    finally { DryRunScope.Current = previous; }
                }
            }
            catch { }
            if (landed) TellThePlanMoved(binding);
        }
    }
}
