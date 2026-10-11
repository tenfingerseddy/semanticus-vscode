// The model facts snapshot, read the way the plan executor's two data checks read it (T268 wiring).
//
// The Jev families and the executor ask two questions about data: which same-table columns go together one to
// one, and whether a column holds only whole numbers (jev-groundwork-contract.md section 1 and 2). This adapter
// answers both from ONE snapshot, looked up by column ref. It never measures anything and never waits: when the
// snapshot has no measured block for a column (no data, a table that was not read, or a column it does not know)
// the answer is null, which every caller treats as "not measured" and refuses on, never as a pass.
//
// A SAMPLE IS NOT A PROOF. A table over the row budget is measured on its first rows only (FactsMeasurer), and
// both questions here are about EVERY row: one unseen fractional value makes "whole numbers" false, one unseen
// pair makes "one to one" false. These answers feed write checks, so for a sampled column they are null (not
// proven) rather than what the sample happened to show. The snapshot itself still carries the sample's reading,
// marked sampled, for anything that only needs a hint.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine.Facts
{
    public sealed class JevModelFactsAdapter : IJevModelFacts, IExactNumberFacts
    {
        private readonly Dictionary<string, ColumnFacts> _byRef;

        public JevModelFactsAdapter(ModelFacts snapshot)
        {
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            _byRef = new Dictionary<string, ColumnFacts>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in (snapshot.Tables ?? Array.Empty<TableFacts>()).SelectMany(t => t.Columns ?? Array.Empty<ColumnFacts>()))
                if (!string.IsNullOrEmpty(c?.Ref)) _byRef[c.Ref] = c;
        }

        /// <summary>The snapshot these answers come from.</summary>
        public ModelFacts Snapshot { get; }

        public IReadOnlyList<string> OneToOneWith(string columnRef) => MeasuredInFull(columnRef)?.OneToOneWith;

        public bool? WholeNumbers(string columnRef) => MeasuredInFull(columnRef)?.WholeNumbers;

        public bool? ExactInDouble(string columnRef) => MeasuredInFull(columnRef)?.ExactInDouble;

        public bool? FitsFixedDecimal(string columnRef) => MeasuredInFull(columnRef)?.FitsFixedDecimal;

        /// <summary>The column's measured facts when every row of its table was read; null when it was not
        /// measured at all or only a sample was.</summary>
        private MeasuredFacts MeasuredInFull(string columnRef) =>
            columnRef != null && _byRef.TryGetValue(columnRef, out var c) && c.Measured != null && !c.Measured.Sampled
                ? c.Measured : null;
    }
}
