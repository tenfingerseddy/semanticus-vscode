// The compose's side of footprints (Plan/PlanFootprint.cs, contract 5.1 to 5.4): the one model read a footprint
// needs, taken on the dispatcher once per compose, and the facts the rows were built from.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>Everything a footprint needs, for the model the facts were read from. One dispatcher read.</summary>
        private async Task<FootprintInputs> FootprintInputsAsync(Session s, PlanFacts f)
        {
            var snapshot = await s.ReadAsync(FootprintSnapshot.Take).ConfigureAwait(false);
            return new FootprintInputs
            {
                Model = snapshot,
                Findings = (IReadOnlyList<ReadinessFinding>)f?.Readiness?.Findings ?? Array.Empty<ReadinessFinding>(),
                UnusedSafe = (f?.Unused?.Items ?? Array.Empty<UnusedItem>())
                    .Where(i => string.Equals(i.Verdict, "safe", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(i.Ref))
                    .Select(i => i.Ref).ToArray(),
                // A cleanup verdict rests on the report reading whenever reports are chosen, read or not.
                ReportsChosen = f != null && (f.MeasuresInReports != null || f.ReportsNeedingChecking > 0
                                              || (f.ReportsUnchecked?.Length ?? 0) > 0),
                StarterRule = f?.StarterRule ?? new List<(string, List<(string, string)>)>(),
                ProposalFamily = ProposalFamily,
                DateMark = f?.DateMark,
                BpaFixTargets = (f?.BpaLines ?? new List<ChangeItem>()).Select(l => l.ObjectRef)
                    .Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.Ordinal).ToArray(),
                WordsFormatTargets = (f?.BpaFormatWords ?? new List<WordsQueueItem>()).Select(i => i.ObjectRef)
                    .Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.Ordinal).ToArray(),
                BpaFixKeyTargets = (f?.BpaLines ?? new List<ChangeItem>())
                    .Where(l => string.Equals(l.Target, "iskey", StringComparison.OrdinalIgnoreCase)).Select(l => l.ObjectRef)
                    .Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.Ordinal).ToArray(),
            };
        }

        /// <summary>The footprint the engine states for one row id and a set of lines, over the open model as it
        /// is now. The commutation gate (PlanCommutationTests) reads the engine's footprints through this, so the
        /// rule it proves is the rule the plan uses, not a copy of it.</summary>
        internal async Task<(string[] Reads, string[] Writes, bool Known)> FootprintForTestAsync(
            string itemId, IReadOnlyList<ModelPlanChangeLine> lines)
        {
            var s = _sessions.Current;
            PlanFacts facts;
            await _modelPlanGate.WaitAsync().ConfigureAwait(false);
            try { facts = await FactsAsync(s).ConfigureAwait(false); }
            finally { _modelPlanGate.Release(); }
            var fp = PlanFootprint.Of(itemId, lines, await FootprintInputsAsync(s, facts).ConfigureAwait(false));
            return (fp.Reads.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
                    fp.Writes.OrderBy(k => k, StringComparer.Ordinal).ToArray(), fp.Known);
        }
    }
}
