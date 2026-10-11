using System;
using System.Collections.Generic;
using System.Linq;
using TabularEditor.TOMWrapper.Undo;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace TabularEditor.TOMWrapper.Utils
{
    /// <summary>
    /// Puts a perspective's tables back the way an edit found them when the edit is taken back.
    ///
    /// Why. Putting a measure, column or hierarchy in a perspective makes the perspective's entry for its table when there
    /// is none, and the wrapper makes that entry straight in TOM with no undo step of its own (PerspectiveIndexer,
    /// EnsurePTExists). Taking the edit back removes the member and leaves the entry behind, empty. A table put in a
    /// perspective that has no members at all makes its entry the same way and is never taken back by Undo. So an edit
    /// that is undone, or a rehearsal that is rolled back, leaves an empty table inside the perspective, and the next
    /// save writes it.
    ///
    /// What this does. A tracked edit's batch is read before and after its work. Each perspective table the work made or
    /// removed is recorded in ONE undo step of its own, inside that batch: undo takes the made ones away and puts the
    /// removed ones back, redo does the reverse. The wrapper's own steps for the members run after (on undo) or before
    /// (on redo) and are no-ops on an entry that is already as they expect.
    ///
    /// Lives in Semanticus.Core because the vendored wrapper is compiled in place here and never modified.
    /// </summary>
    public static class PerspectiveTableUndo
    {
        /// <summary>The perspective tables the model has now, as (perspective, table) pairs of WRAPPER objects. By identity and not by
        /// name: an edit that renames a perspective or a table keeps every pair, and names read here would be stale by the time
        /// the step runs (undoing a perspective's rename dropped its tables, found by the undo-seal class sweep). A wrapper
        /// also survives a delete and its undo, which brings the TOM object back as a new one.</summary>
        public static object Capture(TabularModelHandler handler)
        {
            var set = new HashSet<(object, object)>();
            var db = handler?.Database;
            if (db?.Model == null) return set;
            foreach (var p in db.Model.Perspectives)
            {
                if (!handler.WrapperLookup.TryGetValue(p, out var pw)) continue;
                foreach (var pt in p.PerspectiveTables)
                    if (pt.Table != null && handler.WrapperLookup.TryGetValue(pt.Table, out var tw)) set.Add((pw, tw));
            }
            return set;
        }

        /// <summary>Record one undo step for what the work since <paramref name="before"/> did to perspective tables.
        /// Call inside the edit's batch, after its work and before the batch closes.</summary>
        public static void Seal(TabularModelHandler handler, object before)
        {
            if (handler == null || before is not HashSet<(object, object)> was) return;
            var now = (HashSet<(object, object)>)Capture(handler);
            var made = now.Where(x => !was.Contains(x)).ToList();
            var gone = was.Where(x => !now.Contains(x)).ToList();
            if (made.Count == 0 && gone.Count == 0) return;
            handler.UndoManager.Add(new SidecarUndoAction("restore perspective tables", null,
                undo: () => { Remove(handler, made); Add(handler, gone); },
                redo: () => { Add(handler, made); Remove(handler, gone); }));
        }

        private static void Add(TabularModelHandler handler, List<(object Perspective, object Table)> pairs)
        {
            foreach (var (perspective, table) in pairs)
            {
                var p = (perspective as TabularObject)?.MetadataObject as TOM.Perspective;
                var t = (table as TabularObject)?.MetadataObject as TOM.Table;
                if (p == null || t == null || p.Model == null || t.Model == null || p.PerspectiveTables.Contains(t.Name)) continue;
                p.PerspectiveTables.Add(new TOM.PerspectiveTable { Table = t });
            }
        }

        private static void Remove(TabularModelHandler handler, List<(object Perspective, object Table)> pairs)
        {
            foreach (var (perspective, table) in pairs)
            {
                var p = (perspective as TabularObject)?.MetadataObject as TOM.Perspective;
                var t = (table as TabularObject)?.MetadataObject as TOM.Table;
                if (p != null && t != null && p.Model != null && p.PerspectiveTables.Contains(t.Name)) p.PerspectiveTables.Remove(t.Name);
            }
        }
    }
}
