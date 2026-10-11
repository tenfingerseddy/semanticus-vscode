// Which table "Mark the date table" would mark, when the model says so without a guess (lane E5).
//
// Run the plan applies the Ready Semanticus rows and works out the lines of a row that has none yet (contract
// 7.2). "Mark the date table" can only be one of those rows when the engine can name the table and its date
// column from the model alone. So this answers only when the model's own DAX points at exactly one date column
// on exactly one table that is not the many side of any relationship (a fact table's date is never the date
// table). Anything else, and the row stays the person's to choose on Advanced modelling, as it was.
//
// It never guesses from a name. A table called "Date" with no DAX reading it is not evidence of anything.

using System;
using System.Collections.Generic;
using System.Linq;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    internal sealed class PlanDateMarkCandidate
    {
        public string TableRef;
        public string Table;
        public string Column;
    }

    internal static class PlanDateMark
    {
        /// <summary>The one table and date column the model's DAX reads, or null when there is not exactly one, or
        /// when a table is already marked.</summary>
        public static PlanDateMarkCandidate Candidate(Model m)
        {
            if (m == null) return null;
            if (m.Tables.Any(t => string.Equals(t.DataCategory, "Time", StringComparison.OrdinalIgnoreCase))) return null;

            var (pool, readers) = Pool(m);
            var candidates = readers.SelectMany(r => r.Reads).Distinct().ToList();
            if (candidates.Count != 1) return null;

            var col = candidates[0];
            return new PlanDateMarkCandidate { TableRef = ObjectRefs.For(col.Table), Table = col.Table.Name, Column = col.Name };
        }

        /// <summary>What <see cref="Candidate"/> chooses from, and so what the choice rests on (the date row's footprint
        /// reads it, PlanFootprint.DateTableRow): the date columns that could be the date table's key, which are every
        /// DateTime column on a table that is not the many side of any relationship (a fact table's date is never the
        /// date table, and marking the fact table would be wrong), and each DAX object with the pool columns it reads.</summary>
        public static (HashSet<Column> Pool, List<(IDaxDependantObject Object, Column[] Reads)> Readers) Pool(Model m)
        {
            var pool = new HashSet<Column>();
            var readers = new List<(IDaxDependantObject, Column[])>();
            if (m == null) return (pool, readers);
            var manySide = new HashSet<Table>(m.Relationships.OfType<SingleColumnRelationship>()
                .Select(r => r.FromTable).Where(t => t != null));
            foreach (var c in m.AllColumns)
                if (c.DataType == TabularEditor.TOMWrapper.DataType.DateTime && c.Table != null && !manySide.Contains(c.Table))
                    pool.Add(c);
            foreach (var dep in DaxObjects(m))
            {
                IEnumerable<IDaxObject> keys;
                try { keys = dep.DependsOn.Keys.ToArray(); }
                catch { continue; }
                var reads = keys.OfType<Column>().Where(pool.Contains).Distinct().ToArray();
                if (reads.Length > 0) readers.Add((dep, reads));
            }
            return (pool, readers);
        }

        private static IEnumerable<IDaxDependantObject> DaxObjects(Model m) =>
            m.AllMeasures.Cast<IDaxDependantObject>()
             .Concat(m.AllColumns.OfType<CalculatedColumn>())
             .Concat(m.Tables.OfType<CalculatedTable>())
             // A calculation group's items are where a model often keeps ALL its time intelligence (Northstar does), and
             // DATE-MARK counts them, so the date column they read names the date table too.
             .Concat(m.CalculationGroups.SelectMany(g => g.CalculationItems));
    }
}
